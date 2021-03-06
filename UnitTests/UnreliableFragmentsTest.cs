﻿using NUnit.Framework;
using Pixockets;
using System;
using System.IO;
using System.Net;
using System.Threading;
using Pixockets.Pools;
using UnitTests.Mock;

namespace UnitTests
{
    [TestFixture]
    public class UnreliableFragmentsTest
    {
        MockSmartCallbacks _cbs;
        MockSock _bareSock;
        SmartSock _sock;
        ByteBufferPool _bufferPool;

        [SetUp]
        public void Setup()
        {
            _cbs = new MockSmartCallbacks();
            _bareSock = new MockSock();
            _bufferPool = new ByteBufferPool();
            _sock = new SmartSock(_bufferPool, _bareSock, _cbs);
        }

        [Test]
        public void SendFragmented()
        {
            var endPoint = new IPEndPoint(IPAddress.Loopback, 23452);

            _sock.Connect(endPoint.Address, endPoint.Port);
            Utils.SendConnectResponse(_bareSock, endPoint, _bufferPool);
            var receivedPacket = new ReceivedSmartPacket();
            Assert.IsFalse(_sock.Receive(ref receivedPacket));

            _sock.MaxPayload = 3;

            var ms = new MemoryStream();
            ms.Write(BitConverter.GetBytes((ushort)12345), 0, 2);
            ms.Write(new byte[] { 77 }, 0, 1);
            ms.Write(BitConverter.GetBytes((ushort)23456), 0, 2);
            var buffer = ms.ToArray();
            _sock.Send(endPoint, buffer, 0, buffer.Length, false);

            Assert.AreEqual(3, _bareSock.Sends.Count);

            var packetToSend = _bareSock.Sends[1];
            var header = new PacketHeader();
            header.Init(packetToSend.Buffer, packetToSend.Offset);

            Assert.AreEqual(_sock.MaxPayload + header.HeaderLength, header.Length);
            Assert.AreEqual(12345, BitConverter.ToInt16(packetToSend.Buffer, header.HeaderLength));
            Assert.AreEqual(0, header.SeqNum);
            Assert.IsFalse(header.GetNeedAck());
            Assert.IsTrue(packetToSend.PutBufferToPool, "Unreliable packets should return to pool after send");

            packetToSend = _bareSock.Sends[2];
            header = new PacketHeader();
            header.Init(packetToSend.Buffer, packetToSend.Offset);

            Assert.AreEqual(buffer.Length - _sock.MaxPayload + header.HeaderLength, header.Length);
            Assert.AreEqual(23456, BitConverter.ToInt16(packetToSend.Buffer, header.HeaderLength));
            Assert.AreEqual(1, header.SeqNum);
            Assert.IsFalse(header.GetNeedAck());
            Assert.IsTrue(packetToSend.PutBufferToPool, "Unreliable packets should return to pool after send");
        }

        [Test]
        public void ReceiveFragmented()
        {
            var remoteEndPoint = new IPEndPoint(IPAddress.Loopback, 23452);
            Utils.SendConnectRequest(_bareSock, remoteEndPoint, _bufferPool);

            var buffer1 = CreateFirstFragment();
            _bareSock.FakeReceive(buffer1.Array, buffer1.Offset, buffer1.Count, remoteEndPoint);

            var buffer2 = CreateSecondFragment();
            _bareSock.FakeReceive(buffer2.Array, buffer1.Offset, buffer2.Count, remoteEndPoint);

            AssertCombinedPacketReceived();
        }

        [Test]
        public void DuplicateFragmentsIgnored()
        {
            var remoteEndPoint = new IPEndPoint(IPAddress.Loopback, 23452);
            Utils.SendConnectRequest(_bareSock, remoteEndPoint, _bufferPool);

            var buffer1 = CreateFirstFragment();
            _bareSock.FakeReceive(buffer1.Array, buffer1.Offset, buffer1.Count, remoteEndPoint);
            // Duplicate
            _bareSock.FakeReceive(buffer1.Array, buffer1.Offset, buffer1.Count, remoteEndPoint);

            var buffer2 = CreateSecondFragment();
            _bareSock.FakeReceive(buffer2.Array, buffer1.Offset, buffer2.Count, remoteEndPoint);

            AssertCombinedPacketReceived();
        }

        [Test]
        public void ReceiveSwappedFragments()
        {
            var remoteEndPoint = new IPEndPoint(IPAddress.Loopback, 23452);
            Utils.SendConnectRequest(_bareSock, remoteEndPoint, _bufferPool);

            var buffer2 = CreateSecondFragment();
            _bareSock.FakeReceive(buffer2.Array, buffer2.Offset, buffer2.Count, remoteEndPoint);

            var buffer1 = CreateFirstFragment();
            _bareSock.FakeReceive(buffer1.Array, buffer1.Offset, buffer1.Count, remoteEndPoint);

            AssertCombinedPacketReceived();
        }

        [Test]
        public void ReceiveFailOnTimeoutBetweenFragments()
        {
            _sock.FragmentTimeout = 1;

            var buffer1 = CreateFirstFragment();

            var remoteEndPoint = new IPEndPoint(IPAddress.Loopback, 23452);
            _bareSock.FakeReceive(buffer1.Array, buffer1.Offset, buffer1.Count, remoteEndPoint);
            var receivedPacket = new ReceivedSmartPacket();
            Assert.IsFalse(_sock.Receive(ref receivedPacket));

            // TODO: get rid of sleeps here
            Thread.Sleep(20);
            _sock.Tick();

            var buffer2 = CreateSecondFragment();
            _bareSock.FakeReceive(buffer2.Array, buffer2.Offset, buffer2.Count, remoteEndPoint);

            // Make sure nothing received
            Assert.IsFalse(_sock.Receive(ref receivedPacket));
        }

        private ArraySegment<byte> CreateFirstFragment()
        {
            var header = new PacketHeader();
            header.SetSeqNum(100);
            header.SetFrag(3, 0, 2);
            header.Length = (ushort)(header.HeaderLength + 3);
            var buffer = _bufferPool.Get(header.Length);
            header.WriteTo(buffer, 0);
            Array.Copy(BitConverter.GetBytes((ushort)12345), 0, buffer, header.HeaderLength, 2);
            buffer[header.HeaderLength + 2] = 77;

            return new ArraySegment<byte>(buffer, 0, header.Length);
        }

        private ArraySegment<byte> CreateSecondFragment()
        {
            var header = new PacketHeader();
            header.SetSeqNum(101);
            header.SetFrag(3, 1, 2);
            header.Length = (ushort)(header.HeaderLength + 2);
            var buffer = _bufferPool.Get(header.Length);
            header.WriteTo(buffer, 0);
            Array.Copy(BitConverter.GetBytes((ushort)23456), 0, buffer, header.HeaderLength, 2);

            return new ArraySegment<byte>(buffer, 0, header.Length);
        }

        private void AssertCombinedPacketReceived()
        {
            // Make sure full combined packet received
            var receivedPacket = new ReceivedSmartPacket();
            Assert.IsTrue(_sock.Receive(ref receivedPacket));

            Assert.AreEqual(12345, BitConverter.ToUInt16(receivedPacket.Buffer, receivedPacket.Offset));
            Assert.AreEqual(77, receivedPacket.Buffer[receivedPacket.Offset + 2]);
            Assert.AreEqual(23456, BitConverter.ToUInt16(receivedPacket.Buffer, receivedPacket.Offset + 3));
            Assert.AreEqual(5, receivedPacket.Length);
        }
    }
}

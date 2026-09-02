using System;
using System.Runtime.InteropServices;
using AIGeekTuner.Services.Hardware.Inventory;
using Xunit;

namespace AIGeekTuner.Tests.Services.Hardware.Inventory
{
    /// <summary>
    /// V2-M4.5B Gate 0.1：DEVMODEW 布局回归锁定。
    /// 偏移必须来自 documented ABI（Windows SDK um\wingdi.h
    /// <c>typedef struct _devicemodeW</c>，x64 自然对齐），而不是实机校准魔数。
    /// 本测试把 <see cref="DevModeWLayout"/>（经 Marshal.OffsetOf 从镜像结构推导）
    /// 与 documented 值互相锁定：任一侧被改坏都会失败。
    /// </summary>
    public sealed class GdiDevModeWLayoutTests
    {
        // ---- documented DEVMODEW offsets（wingdi.h，x64）----
        private const int DocumentedOffsetDmSize = 68;
        private const int DocumentedOffsetDmFields = 72;
        private const int DocumentedOffsetPelsWidth = 172;
        private const int DocumentedOffsetPelsHeight = 176;
        private const int DocumentedOffsetDisplayFrequency = 184;
        private const int DocumentedStructSize = 220;

        [Fact]
        public void OffsetOf_DerivedOffsets_MatchDocumentedAbi()
        {
            Assert.Equal(DocumentedOffsetDmSize, DevModeWLayout.OffsetSize);
            Assert.Equal(DocumentedOffsetDmFields, DevModeWLayout.OffsetFields);
            Assert.Equal(DocumentedOffsetPelsWidth, DevModeWLayout.OffsetPelsWidth);
            Assert.Equal(DocumentedOffsetPelsHeight, DevModeWLayout.OffsetPelsHeight);
            Assert.Equal(DocumentedOffsetDisplayFrequency, DevModeWLayout.OffsetDisplayFrequency);
            Assert.Equal(DocumentedStructSize, DevModeWLayout.StructSize);
        }

        [Fact]
        public void MirrorStruct_MarshalSizeOf_MatchesDocumentedSize()
        {
            Assert.Equal(DocumentedStructSize, Marshal.SizeOf<DevModeW>());
        }

        [Fact]
        public void ParseCurrentDevMode_SyntheticBufferPerDocumentedAbi_ParsesFields()
        {
            var buffer = Marshal.AllocHGlobal(DevModeWLayout.StructSize + 64);
            try
            {
                Zero(buffer);
                Marshal.WriteInt16(buffer, DevModeWLayout.OffsetSize, (short)DevModeWLayout.StructSize);
                Marshal.WriteInt32(
                    buffer,
                    DevModeWLayout.OffsetFields,
                    (int)(DevModeWLayout.DMPelsWidth | DevModeWLayout.DMPelsHeight
                        | DevModeWLayout.DMDisplayFrequency));
                Marshal.WriteInt32(buffer, DevModeWLayout.OffsetPelsWidth, 2560);
                Marshal.WriteInt32(buffer, DevModeWLayout.OffsetPelsHeight, 1600);
                Marshal.WriteInt32(buffer, DevModeWLayout.OffsetDisplayFrequency, 240);

                var monitorDeviceId = @"MONITOR\BOE0B35\{e2f4-4d3}";
                var descriptor = GdiDisplayModeSource.ParseCurrentDevMode(
                    buffer, "DISPLAY1", monitorDeviceId, isPrimary: true);

                Assert.NotNull(descriptor);
                Assert.Equal(2560, descriptor!.Width);
                Assert.Equal(1600, descriptor.Height);
                Assert.Equal(240u, descriptor.RefreshRateHz);
                Assert.True(descriptor.IsPrimary);
                Assert.Equal("DISPLAY1", descriptor.DeviceName);
                Assert.Equal(monitorDeviceId, descriptor.MonitorDeviceId);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        [Fact]
        public void ParseCurrentDevMode_MissingPelsFields_ReturnsNull()
        {
            var buffer = Marshal.AllocHGlobal(DevModeWLayout.StructSize + 64);
            try
            {
                Zero(buffer);
                Marshal.WriteInt32(buffer, DevModeWLayout.OffsetFields, (int)DevModeWLayout.DMDisplayFrequency);
                Marshal.WriteInt32(buffer, DevModeWLayout.OffsetPelsWidth, 2560);
                Marshal.WriteInt32(buffer, DevModeWLayout.OffsetPelsHeight, 1600);

                Assert.Null(GdiDisplayModeSource.ParseCurrentDevMode(buffer, "DISPLAY1", null, false));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        [Fact]
        public void ParseCurrentDevMode_MissingFrequencyFlag_ReportsZeroFrequency()
        {
            var buffer = Marshal.AllocHGlobal(DevModeWLayout.StructSize + 64);
            try
            {
                Zero(buffer);
                Marshal.WriteInt32(
                    buffer,
                    DevModeWLayout.OffsetFields,
                    (int)(DevModeWLayout.DMPelsWidth | DevModeWLayout.DMPelsHeight));
                Marshal.WriteInt32(buffer, DevModeWLayout.OffsetPelsWidth, 1920);
                Marshal.WriteInt32(buffer, DevModeWLayout.OffsetPelsHeight, 1080);
                Marshal.WriteInt32(buffer, DevModeWLayout.OffsetDisplayFrequency, 999);

                var descriptor = GdiDisplayModeSource.ParseCurrentDevMode(buffer, "DISPLAY1", null, false);

                Assert.NotNull(descriptor);
                Assert.Equal(0u, descriptor!.RefreshRateHz);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        [Fact]
        public void ParseCurrentDevMode_NonPositiveDimensions_ReturnsNull()
        {
            var buffer = Marshal.AllocHGlobal(DevModeWLayout.StructSize + 64);
            try
            {
                Zero(buffer);
                Marshal.WriteInt32(
                    buffer,
                    DevModeWLayout.OffsetFields,
                    (int)(DevModeWLayout.DMPelsWidth | DevModeWLayout.DMPelsHeight));

                Assert.Null(GdiDisplayModeSource.ParseCurrentDevMode(buffer, "DISPLAY1", null, false));
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static unsafe void Zero(IntPtr pointer)
        {
            var p = (byte*)pointer;
            for (var i = 0; i < DevModeWLayout.StructSize + 64; i++)
            {
                p[i] = 0;
            }
        }
    }
}

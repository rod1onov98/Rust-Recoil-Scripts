using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Diagnostics;

namespace RustRecoilControl
{
    public class VMultiDriver : IDisposable
    {
        private const string VMultiDevicePath = ""; // here you need put path to vmulti virtual mouse 

        private FileStream _deviceStream;
        private bool _isConnected = false;
        private bool _disposed = false;

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct VMultiMouseReport
        {
            public byte ReportId;
            public byte Buttons;
            public short X;
            public short Y;
            public byte Wheel;

            public VMultiMouseReport(byte reportId, byte buttons, short x, short y, byte wheel)
            {
                ReportId = reportId;
                Buttons = buttons;
                X = x;
                Y = y;
                Wheel = wheel;
            }
        }

        public bool Connect()
        {
            try
            {
                _deviceStream = new FileStream(VMultiDevicePath, FileMode.Open, FileAccess.Write, FileShare.Write);
                _isConnected = true;
                Debug.WriteLine("VMulti driver connected successfully");
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"VMulti connection failed: {ex.Message}");
                Debug.WriteLine($"Device path: {VMultiDevicePath}");
                _isConnected = false;
                return false;
            }
        }

        public void SendMouseMove(short x, short y)
        {
            if (!_isConnected && !Connect())
            {
                Debug.WriteLine("VMulti not connected, cannot send mouse movement");
                return;
            }

            try
            {
                var report = new VMultiMouseReport(0x01, 0x00, x, y, 0x00);

                byte[] reportBytes = new byte[Marshal.SizeOf(typeof(VMultiMouseReport))];
                IntPtr ptr = Marshal.AllocHGlobal(reportBytes.Length);

                Marshal.StructureToPtr(report, ptr, false);
                Marshal.Copy(ptr, reportBytes, 0, reportBytes.Length);
                Marshal.FreeHGlobal(ptr);

                _deviceStream.Write(reportBytes, 0, reportBytes.Length);
                _deviceStream.Flush();

                Debug.WriteLine($"VMulti move: X={x}, Y={y}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"VMulti send error: {ex.Message}");
                _isConnected = false;
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _deviceStream?.Close();
                    _deviceStream?.Dispose();
                }
                _disposed = true;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
            Debug.WriteLine("VMulti driver disposed");
        }

        ~VMultiDriver()
        {
            Dispose(false);
        }
    }
}
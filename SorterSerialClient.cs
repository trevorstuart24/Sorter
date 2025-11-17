using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Text;
using System.Threading.Tasks;

namespace Sorter
{
    /// <summary>
    /// Thin, deterministic wrapper over SerialPort that:
    /// - Manages connection/disconnection to the sorter firmware.
    /// - Sends commands as lines.
    /// - Tracks "done" signals and lets callers await them with WaitForDoneAsync.
    /// It does NOT know anything about LLMs or cartridges.
    /// </summary>
    public sealed class SorterSerialClient : IDisposable
    {
        private readonly object _lock = new object();
        private SerialPort _serial;
        private bool _disposed;

        // Raw lines received, mostly for debugging.
        private readonly List<string> _lines = new List<string>();

        // Waiters for "done" events.
        private readonly List<TaskCompletionSource<bool>> _waitDoneAwaiters =
            new List<TaskCompletionSource<bool>>();

        // Count of unconsumed "done" signals that arrived before a waiter was registered.
        private int _pendingDoneSignals;

        // Optional logging callback.
        private readonly Action<string> _log;

        public bool IsConnected
        {
            get
            {
                lock (_lock)
                {
                    return _serial != null && _serial.IsOpen;
                }
            }
        }

        public SorterSerialClient(Action<string> log = null)
        {
            _log = log;
        }

        public void Connect(string portName)
        {
            if (string.IsNullOrWhiteSpace(portName))
                throw new ArgumentException("Port name is required.", nameof(portName));

            lock (_lock)
            {
                ThrowIfDisposed();

                if (_serial != null)
                {
                    DisconnectInternal_NoLock();
                }

                var sp = new SerialPort(portName, 9600, Parity.None, 8, StopBits.One)
                {
                    Handshake = Handshake.None,
                    NewLine = "\n",
                    Encoding = Encoding.ASCII,
                    ReadTimeout = 500,
                    WriteTimeout = 500
                };

                sp.DataReceived += SerialOnDataReceived;
                sp.Open();

                _serial = sp;
                _lines.Clear();
                _waitDoneAwaiters.Clear();
                _pendingDoneSignals = 0;

                Log($"Serial connected: {portName}");
            }
        }

        public void Disconnect()
        {
            lock (_lock)
            {
                DisconnectInternal_NoLock();
            }
        }

        private void DisconnectInternal_NoLock()
        {
            if (_serial != null)
            {
                try
                {
                    _serial.DataReceived -= SerialOnDataReceived;
                }
                catch
                {
                }

                try
                {
                    if (_serial.IsOpen)
                    {
                        _serial.Close();
                    }
                }
                catch
                {
                }

                try
                {
                    _serial.Dispose();
                }
                catch
                {
                }

                _serial = null;
            }

            // Fail any pending "done" waiters with false (timeout/abort).
            if (_waitDoneAwaiters.Count > 0)
            {
                var waiters = _waitDoneAwaiters.ToArray();
                _waitDoneAwaiters.Clear();
                foreach (var tcs in waiters)
                {
                    tcs.TrySetResult(false);
                }
            }

            _pendingDoneSignals = 0;
            Log("Serial disconnected.");
        }

        /// <summary>
        /// Sends a single command line to the firmware. Newline is appended if missing.
        /// Throws if not connected.
        /// </summary>
        public void Send(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
                throw new ArgumentException("Command cannot be empty.", nameof(command));

            lock (_lock)
            {
                ThrowIfDisposed();

                if (_serial == null || !_serial.IsOpen)
                    throw new InvalidOperationException("Serial port is not connected.");

                string toSend = command;
                if (!toSend.EndsWith("\n"))
                    toSend += "\n";

                try
                {
                    _serial.Write(toSend);
                    Log("TX: " + command);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException("Failed to send serial command: " + ex.Message, ex);
                }
            }
        }

        /// <summary>
        /// Waits for the next "done" line from firmware.
        /// Returns true if a "done" arrives before the timeout, false on timeout or disconnect.
        /// If a "done" already arrived and was not yet consumed, returns immediately.
        /// </summary>
        public Task<bool> WaitForDoneAsync(int timeoutMs)
        {
            ThrowIfDisposed();

            lock (_lock)
            {
                if (_serial == null || !_serial.IsOpen)
                    return Task.FromResult(false);

                // If we already have a pending done signal, consume it immediately.
                if (_pendingDoneSignals > 0)
                {
                    _pendingDoneSignals--;
                    return Task.FromResult(true);
                }

                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waitDoneAwaiters.Add(tcs);

                if (timeoutMs <= 0)
                {
                    // No timeout, just await the tcs.
                    return tcs.Task;
                }

                var delayTask = Task.Delay(timeoutMs);

                _ = delayTask.ContinueWith(t =>
                {
                    bool completed = false;
                    lock (_lock)
                    {
                        if (_waitDoneAwaiters.Remove(tcs))
                        {
                            completed = tcs.TrySetResult(false);
                        }
                    }

                    if (completed)
                    {
                        Log($"WaitForDoneAsync timeout after {timeoutMs} ms.");
                    }
                }, TaskScheduler.Default);

                return tcs.Task;
            }
        }

        private void SerialOnDataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            SerialPort sp;
            lock (_lock)
            {
                sp = _serial;
            }

            if (sp == null)
                return;

            try
            {
                while (sp.BytesToRead > 0)
                {
                    string line;
                    try
                    {
                        line = sp.ReadLine();
                    }
                    catch (TimeoutException)
                    {
                        break;
                    }

                    if (line == null)
                        break;

                    HandleLine(line);
                }
            }
            catch (Exception ex)
            {
                Log("Serial read error: " + ex.Message);
            }
        }

        private void HandleLine(string rawLine)
        {
            if (rawLine == null)
                return;

            string line = rawLine.Trim('\r', '\n');
            if (line.Length == 0)
                return;

            Log("RX: " + line);

            lock (_lock)
            {
                _lines.Add(line);

                if (string.Equals(line, "done", StringComparison.OrdinalIgnoreCase))
                {
                    if (_waitDoneAwaiters.Count > 0)
                    {
                        var tcs = _waitDoneAwaiters[0];
                        _waitDoneAwaiters.RemoveAt(0);
                        tcs.TrySetResult(true);
                    }
                    else
                    {
                        _pendingDoneSignals++;
                    }
                }
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(SorterSerialClient));
        }

        private void Log(string message)
        {
            if (_log == null)
                return;

            try
            {
                _log(message);
            }
            catch
            {
                // Ignore logging failures.
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            lock (_lock)
            {
                DisconnectInternal_NoLock();
            }
        }
    }
}

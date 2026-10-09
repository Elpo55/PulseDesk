using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Sysora.Core.Interfaces;

namespace Sysora.Infrastructure.Windows;

/// <summary>
/// Sleep and resume notifications through <c>PowerRegisterSuspendResumeNotification</c> (a callback, so no window is
/// needed). Registered on first subscription; unregistered on dispose.
/// </summary>
public sealed unsafe partial class WindowsPowerEvents : ISystemPowerEvents, IDisposable
{
    private const uint DeviceNotifyCallback = 2;
    private const uint SuspendEvent = 0x4;          // PBT_APMSUSPEND
    private const uint ResumeAutomaticEvent = 0x12; // PBT_APMRESUMEAUTOMATIC: sent on every resume

    private readonly ILogger<WindowsPowerEvents> _logger;
    private readonly Lock _lock = new();
    private GCHandle _self;
    private nint _registration;
    private bool _disposed;
    private EventHandler? _suspending;
    private EventHandler? _resumed;

    public WindowsPowerEvents(ILogger<WindowsPowerEvents> logger)
    {
        _logger = logger;
    }

    public event EventHandler? Suspending
    {
        add
        {
            lock (_lock)
            {
                _suspending += value;
                EnsureRegistered();
            }
        }

        remove
        {
            lock (_lock)
            {
                _suspending -= value;
            }
        }
    }

    public event EventHandler? Resumed
    {
        add
        {
            lock (_lock)
            {
                _resumed += value;
                EnsureRegistered();
            }
        }

        remove
        {
            lock (_lock)
            {
                _resumed -= value;
            }
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_registration != 0)
            {
                _ = PowerUnregisterSuspendResumeNotification(_registration);
                _registration = 0;
            }

            if (_self.IsAllocated)
            {
                _self.Free();
            }
        }
    }

    /// <summary>Caller holds the lock.</summary>
    private void EnsureRegistered()
    {
        if (_registration != 0 || _disposed)
        {
            return;
        }

        _self = GCHandle.Alloc(this);
        var parameters = new DeviceNotifySubscribeParameters
        {
            Callback = (nint)(delegate* unmanaged<nint, uint, nint, uint>)&OnPowerChanged,
            Context = GCHandle.ToIntPtr(_self),
        };
        var error = PowerRegisterSuspendResumeNotification(DeviceNotifyCallback, ref parameters, out _registration);
        if (error != 0)
        {
            _registration = 0;
            _self.Free();
            _logger.LogWarning("Sleep and resume notifications are not available (error {Error}).", error);
        }
    }

    [UnmanagedCallersOnly]
    private static uint OnPowerChanged(nint context, uint type, nint setting)
    {
        try
        {
            if (GCHandle.FromIntPtr(context).Target is WindowsPowerEvents events)
            {
                events.Raise(type);
            }
        }
        catch (Exception ex)
        {
            // Exceptions must never cross back into native code.
            System.Diagnostics.Debug.WriteLine(ex);
        }

        return 0;
    }

    private void Raise(uint type)
    {
        EventHandler? handler;
        lock (_lock)
        {
            handler = type switch
            {
                SuspendEvent => _suspending,
                ResumeAutomaticEvent => _resumed,
                _ => null,
            };
        }

        handler?.Invoke(this, EventArgs.Empty);
    }

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerRegisterSuspendResumeNotification(uint flags, ref DeviceNotifySubscribeParameters recipient, out nint registrationHandle);

    [LibraryImport("powrprof.dll")]
    private static partial uint PowerUnregisterSuspendResumeNotification(nint registrationHandle);

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceNotifySubscribeParameters
    {
        public nint Callback;
        public nint Context;
    }
}

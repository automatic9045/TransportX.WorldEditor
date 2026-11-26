using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using Avalonia;
using Avalonia.Threading;

namespace TransportX.WorldEditor.Tests;

internal static class AvaloniaUiTestHost
{
    private static readonly object InitLock = new();
    private static bool Initialized;

    public static void Run(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        EnsureStarted();

        ExceptionDispatchInfo? captured = null;
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                captured = ExceptionDispatchInfo.Capture(ex);
            }
        }).Wait();

        captured?.Throw();
    }

    private static void EnsureStarted()
    {
        lock (InitLock)
        {
            if (Initialized)
            {
                return;
            }

            ManualResetEventSlim ready = new(false);
            Exception? initError = null;
            Thread uiThread = new(() =>
            {
                try
                {
                    if (Application.Current is null)
                    {
                        Program.BuildAvaloniaApp().SetupWithoutStarting();
                    }

                    SynchronizationContext.SetSynchronizationContext(
                        new AvaloniaSynchronizationContext());
                    ready.Set();
                    Dispatcher.UIThread.MainLoop(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    initError = ex;
                    ready.Set();
                }
            });

            if (OperatingSystem.IsWindows())
            {
                uiThread.SetApartmentState(ApartmentState.STA);
            }

            uiThread.IsBackground = true;
            uiThread.Start();
            ready.Wait();

            if (initError is not null)
            {
                throw new InvalidOperationException("Failed to initialize Avalonia UI test host.", initError);
            }

            Initialized = true;
        }
    }
}

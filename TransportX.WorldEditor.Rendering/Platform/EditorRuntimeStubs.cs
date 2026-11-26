using System;
using System.Collections.Generic;
using Silk.NET.Input;
using TransportX.Audio;
using TransportX.Input;
using TransportX.Input.Configuration;
using Vortice.DirectInput;
using Vortice.XAudio2;

namespace TransportX.WorldEditor.Rendering.Platform;

/// <summary>
/// No-op <see cref="IInputContext"/> used when hosting <see cref="TransportX"/> inside Avalonia.
/// </summary>
public sealed class DummyInputContext : IInputContext
{
    public nint Handle => 0;
    public IReadOnlyList<IGamepad> Gamepads { get; } = Array.Empty<IGamepad>();
    public IReadOnlyList<IJoystick> Joysticks { get; } = Array.Empty<IJoystick>();
    public IReadOnlyList<IKeyboard> Keyboards { get; } = Array.Empty<IKeyboard>();
    public IReadOnlyList<IMouse> Mice { get; } = Array.Empty<IMouse>();
    public IReadOnlyList<IInputDevice> OtherDevices { get; } = Array.Empty<IInputDevice>();

    public event Action<IInputDevice, bool>? ConnectionChanged { add { } remove { } }

    public void Dispose()
    {
    }
}

/// <summary>
/// Silent no-op <see cref="IAudioHost"/> so edit mode never initializes or emits audio streams.
/// </summary>
public sealed class DummyAudioHost : IAudioHost
{
    public IXAudio2 XAudio2 => null!;
    public IXAudio2MasteringVoice MasteringVoice => null!;
    public X3DAudio X3DAudio => default!;

    public ISoundSource CreateSoundSource(float maxFrequencyRatio, AudioChannelType channelType = AudioChannelType.Stereo)
    {
        return ISoundSource.Empty;
    }

    public ISoundSource3D CreateSoundSource3D(float maxFrequencyRatio)
    {
        return ISoundSource3D.Empty;
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// Silent no-op <see cref="IAudioClient"/> paired with <see cref="DummyAudioHost"/>.
/// </summary>
public sealed class DummyAudioClient : IAudioClient
{
    public Listener Listener { get; } = new Listener();
}

/// <summary>
/// Stub <see cref="IInputHost"/> for <c>EditorWorld</c> (real viewport input is routed via Avalonia/HWND).
/// </summary>
public sealed class DummyInputHost : IInputHost
{
    public IInputContext SilkContext { get; } = new DummyInputContext();
    public IDirectInput8 DirectInput => null!;
    public IReadOnlyDictionary<Guid, JoystickState> JoystickStates { get; } = new Dictionary<Guid, JoystickState>();

    public void RefreshDevices()
    {
    }

    public void Tick(TimeSpan elapsed)
    {
    }

    public void Dispose()
    {
        SilkContext.Dispose();
    }
}

/// <summary>
/// Silent no-op <see cref="IInputClient"/> for <c>EditorWorld</c> and <c>ScriptVerificationRunner</c>.
/// </summary>
public sealed class DummyInputClient : IInputClient
{
    public IReadOnlyDictionary<string, InputProfile> Profiles { get; } = new Dictionary<string, InputProfile>();

    public event MouseScrollEventHandler? MouseScroll { add { } remove { } }
    public event MouseMoveEventHandler? MouseMove { add { } remove { } }

    public KeyObserver ObserveKey(Silk.NET.Input.Key key) => new(key);
    public JoystickButtonObserver ObserveJoystickButton(Guid deviceGuid, int buttonIndex) => new(deviceGuid, buttonIndex);
    public JoystickAxisObserver ObserveJoystickAxis(Guid deviceGuid, JoystickAxisType axisType) => new(deviceGuid, axisType);
    public JoystickPovObserver ObserveJoystickPov(Guid deviceGuid, int povIndex) => new(deviceGuid, povIndex);

    public void Tick(TimeSpan elapsed)
    {
    }
}

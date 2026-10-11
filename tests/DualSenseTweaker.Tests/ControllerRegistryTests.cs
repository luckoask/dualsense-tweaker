using DualSenseTweaker.Core.Device;
using DualSenseTweaker.Core.Profiles;
using DualSenseTweaker.Core.State;

namespace DualSenseTweaker.Tests;

public sealed class ControllerRegistryTests : IDisposable
{
    private static readonly ControllerInfo First = new(@"\\?\hid#first", DualSenseProtocol.DualSenseProductId, ConnectionType.Bluetooth, "AA:AA:AA:AA:AA:01");
    private static readonly ControllerInfo Second = new(@"\\?\hid#second", DualSenseProtocol.DualSenseProductId, ConnectionType.Bluetooth, "AA:AA:AA:AA:AA:02");
    private static readonly ControllerInfo Third = new(@"\\?\hid#third", DualSenseProtocol.DualSenseProductId, ConnectionType.Usb, "AA:AA:AA:AA:AA:03");
    private static readonly RgbColor Red = new(255, 0, 0);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "DualSenseTweakerTests", Guid.NewGuid().ToString("N"));
    private readonly SettingsState _editor = new();
    private readonly LiveOutput _live = new();
    private readonly RefreshState _refresh = new();
    private readonly PreferencesState _preferences = new();
    private readonly ProfileService _profiles;
    private readonly ControllerRegistry _registry;

    public ControllerRegistryTests()
    {
        _profiles = new ProfileService(new ProfileStore(_root), _editor, _refresh, _preferences);
        _profiles.Activate(ProfilePresets.CustomName);
        _registry = new ControllerRegistry(_profiles, _editor, _live, _refresh, _preferences);
    }

    public void Dispose()
    {
        _registry.Dispose();
        _profiles.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    [Fact]
    public void NewControllersGetANameAndTheCurrentProfile()
    {
        _registry.SourcesFor(First);
        _registry.SourcesFor(Second);

        Assert.Equal(
            [new ControllerBinding(First.Key, "Ovládač 1", "Custom"), new ControllerBinding(Second.Key, "Ovládač 2", "Custom")],
            _registry.Bindings);
        Assert.Equal(["Ovládač 2"], _registry.LinkedWith(First.Key));
    }

    [Fact]
    public void LinkedControllersFollowEveryEditLive()
    {
        var first = _registry.SourcesFor(First)!;
        var second = _registry.SourcesFor(Second)!;

        _editor.UpdateOutput(o => o with { LightbarColor = Red });
        _live.Replace(new LiveOverlay { RumbleHeavy = 77 });

        Assert.Equal(Red, first.Settings.Current.Output.LightbarColor);
        Assert.Equal(Red, second.Settings.Current.Output.LightbarColor);
        Assert.Equal(77, second.Live.Current.RumbleHeavy);
    }

    [Fact]
    public void AControllerOnItsOwnProfileKeepsItWhileTheOtherIsEdited()
    {
        _registry.SetSelected(First.Key);
        _registry.SourcesFor(First);
        var second = _registry.SourcesFor(Second)!;

        _registry.Assign(Second.Key, ProfilePresets.AdventureName);
        _editor.UpdateOutput(o => o with { LightbarColor = Red });

        Assert.Equal(ProfilePresets.Adventure().Output.LightbarColor, second.Settings.Current.Output.LightbarColor);
        Assert.Equal(ProfilePresets.CustomName, _profiles.Active.Name); // the edited controller did not move
        Assert.Empty(_registry.LinkedWith(First.Key));
    }

    [Fact]
    public void SelectingAnotherControllerOpensItsProfileWithoutMovingTheOthers()
    {
        _registry.SourcesFor(First);
        _registry.SourcesFor(Second);
        _registry.Assign(Second.Key, ProfilePresets.AdventureName);
        _registry.SetSelected(First.Key);

        _registry.SetSelected(Second.Key);

        Assert.Equal(ProfilePresets.AdventureName, _profiles.Active.Name);
        Assert.Equal(ProfilePresets.CustomName, _registry.Find(First.Key)!.Profile);
        Assert.Equal(Second.Key, _preferences.Current.SelectedController);
    }

    [Fact]
    public void SwitchingProfileElsewhereMovesTheEditedControllerAndItsLinkedOnes()
    {
        _registry.SourcesFor(First);
        _registry.SourcesFor(Second);
        _registry.SourcesFor(Third);
        _registry.Assign(Third.Key, ProfilePresets.AdventureName);
        _registry.SetSelected(First.Key);

        _profiles.Activate(ProfilePresets.CompetitiveName); // profile tile, hotkey or a running game

        Assert.Equal(ProfilePresets.CompetitiveName, _registry.Find(First.Key)!.Profile);
        Assert.Equal(ProfilePresets.CompetitiveName, _registry.Find(Second.Key)!.Profile);
        Assert.Equal(ProfilePresets.AdventureName, _registry.Find(Third.Key)!.Profile);
    }

    [Fact]
    public void LinkOthersCopiesTheSettingsAndKeepsThemInSync()
    {
        _registry.SetSelected(First.Key);
        _registry.SourcesFor(First);
        var second = _registry.SourcesFor(Second)!;
        _registry.Assign(Second.Key, ProfilePresets.AdventureName);

        _registry.LinkOthers(First.Key);
        _editor.UpdateOutput(o => o with { LightbarColor = Red });

        Assert.Equal(ProfilePresets.CustomName, _registry.Find(Second.Key)!.Profile);
        Assert.Equal(Red, second.Settings.Current.Output.LightbarColor);
    }

    [Fact]
    public void AnOwnCopyUnlinksWithTheSameSettings()
    {
        _registry.SetSelected(First.Key);
        _registry.SourcesFor(First);
        var second = _registry.SourcesFor(Second)!;
        _editor.UpdateOutput(o => o with { LightbarColor = Red });

        var copy = _registry.MakeIndependent(Second.Key);
        _editor.UpdateOutput(o => o with { LightbarColor = new RgbColor(0, 255, 0) });

        Assert.NotNull(copy);
        Assert.Equal("Ovládač 2", copy.Name);
        Assert.Equal(copy.Name, _registry.Find(Second.Key)!.Profile);
        Assert.Equal(Red, second.Settings.Current.Output.LightbarColor);
        Assert.Empty(_registry.LinkedWith(First.Key));
    }

    [Fact]
    public void FactoryResetForgetsProfileAndCalibrationAndReleasesTheController()
    {
        _registry.SourcesFor(First);
        _preferences.Update(p => p with { Gyro = p.Gyro.WithCalibration(new GyroCalibrationEntry(First.Key, 1, 2, 3)) });

        _registry.FactoryReset(First.Key);

        Assert.Null(_registry.Find(First.Key)!.Profile);
        Assert.Null(_registry.SourcesFor(First));
        Assert.Null(_preferences.Current.Gyro.Find(First.Key));

        _registry.Assign(First.Key, ProfilePresets.CustomName); // taken over again
        Assert.NotNull(_registry.SourcesFor(First));
    }

    [Fact]
    public void ADeletedProfileHandsItsControllersToTheActiveOne()
    {
        _registry.SetSelected(First.Key);
        _registry.SourcesFor(First);
        _registry.SourcesFor(Second);
        var own = _registry.MakeIndependent(Second.Key)!;

        _profiles.Delete(own.Name);

        Assert.Equal(_profiles.Active.Name, _registry.Find(Second.Key)!.Profile);
    }

    [Fact]
    public void BindingsAreSavedWithTheAppSettings()
    {
        _registry.SourcesFor(First);
        _registry.Rename(First.Key, "Môj biely");
        _profiles.Dispose(); // flushes settings.json

        var saved = new ProfileStore(_root).LoadAppSettings().Preferences.Controllers;
        Assert.Equal([new ControllerBinding(First.Key, "Môj biely", "Custom")], saved);
    }

    [Fact]
    public void FactoryResetReportRestoresTheFreshLook()
    {
        var report = OutputReportBuilder.BuildFactoryReset(ConnectionType.Usb, 0, useVibrationV2: true);
        var c = report.AsSpan(1);
        Assert.Equal(0x05, c[DualSenseProtocol.Common.RightTrigger]); // triggers off
        Assert.Equal(0x05, c[DualSenseProtocol.Common.LeftTrigger]);
        Assert.Equal(0, c[DualSenseProtocol.Common.MotorPower]);
        Assert.Equal(0, c[DualSenseProtocol.Common.PlayerLeds]);
        Assert.Equal(0, c[DualSenseProtocol.Common.MuteButtonLed]);
        Assert.Equal([0, 0, 128], c.Slice(DualSenseProtocol.Common.LightbarRed, 3).ToArray());
        Assert.NotEqual(0, c[DualSenseProtocol.Common.ValidFlag1] & DualSenseProtocol.Flag1.LightbarControl);
        Assert.NotEqual(0, c[DualSenseProtocol.Common.ValidFlag1] & DualSenseProtocol.Flag1.PlayerIndicator);
    }
}

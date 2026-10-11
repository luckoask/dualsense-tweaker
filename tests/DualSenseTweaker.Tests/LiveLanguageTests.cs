using DualSenseTweaker.Core.Device;
using DualSenseTweaker.Core.Localization;
using DualSenseTweaker.Core.State;
using DualSenseTweaker.ViewModels;

namespace DualSenseTweaker.Tests;

[CollectionDefinition("Application language", DisableParallelization = true)]
public sealed class ApplicationLanguageCollection { }

[Collection("Application language")]
public sealed class LiveLanguageTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExistingTriggerEditorUpdatesEveryModeLabelWithoutChangingItsEffect(bool left)
    {
        var originalLanguage = TextCatalog.Language;
        try
        {
            TextCatalog.Language = AppLanguage.Slovak;
            var state = new SettingsState();
            state.UpdateOutput(o => o with
            {
                LightbarEffect = LightbarEffectMode.Rainbow,
                LeftTrigger = new TriggerEffect { Mode = TriggerMode.Resistance, StartPosition = 2, Strength = 5 },
                RightTrigger = new TriggerEffect { Mode = TriggerMode.Vibration, StartPosition = 3, Amplitude = 6, Frequency = 80 },
            });
            var before = state.Read();
            var view = new TriggerEditorViewModel(state, left ? "01" : "02", left ? "L2" : "R2",
                o => left ? o.LeftTrigger : o.RightTrigger,
                (o, effect) => left ? o with { LeftTrigger = effect } : o with { RightTrigger = effect },
                left ? OutputGroups.LeftTrigger : OutputGroups.RightTrigger, _ => false, _ => { });
            var notifications = new List<string?>();
            view.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
            var languages = new[] { AppLanguage.Slovak, AppLanguage.English, AppLanguage.Czech, AppLanguage.Slovak };
            string[][] labels =
            [
                ["Hra", "Vypnuté", "Odpor", "Cvaknutie", "Vibrácia"],
                ["Game", "Off", "Resistance", "Click", "Vibration"],
                ["Hra", "Vypnuto", "Odpor", "Cvaknutí", "Vibrace"],
                ["Hra", "Vypnuté", "Odpor", "Cvaknutie", "Vibrácia"],
            ];
            for (int i = 0; i < languages.Length; i++)
            {
                TextCatalog.Language = languages[i];
                view.RefreshTranslations();
                Assert.Equal(labels[i], view.ModeOptions.Select(option => option.Label));
                Assert.Equal(new[] { TriggerMode.Game, TriggerMode.Off, TriggerMode.Resistance, TriggerMode.Section, TriggerMode.Vibration },
                    view.ModeOptions.Select(option => option.Value));
                Assert.Equal(left ? TriggerMode.Resistance : TriggerMode.Vibration, view.Mode);
                Assert.Equal(before, state.Read());
            }
            Assert.Equal(languages.Length, notifications.Count(name => name == string.Empty));
        }
        finally { TextCatalog.Language = originalLanguage; }
    }
}

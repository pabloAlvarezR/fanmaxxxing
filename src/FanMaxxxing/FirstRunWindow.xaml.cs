using System.Windows;
using System.Windows.Controls;
using FanMaxxxing.Core;

namespace FanMaxxxing;

/// <summary>
/// Bienvenida de la primera vez: idioma y sonido. Los textos se cambian en vivo al elegir idioma.
/// Si se cierra sin elegir (Alt+F4), se queda el idioma de Windows y el sonido activado.
/// </summary>
public partial class FirstRunWindow : Window
{
    private readonly AppSettings _settings;

    public FirstRunWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();

        string current = Loc.Current;
        foreach (var (code, name) in Loc.Languages)
        {
            var rb = new RadioButton
            {
                Style = (Style)FindResource("Pill"),
                GroupName = "lang",
                Content = name,
                Tag = code,
                IsChecked = code == current,
            };
            rb.Checked += Language_Checked;
            LanguagePanel.Children.Add(rb);
        }
        ApplyTexts();
    }

    private void ApplyTexts()
    {
        TitleText.Text = Loc.T("welcome.title");
        SubtitleText.Text = Loc.T("welcome.subtitle");
        LanguageLabel.Text = Loc.T("welcome.language");
        SoundLabel.Text = Loc.T("welcome.sound.q");
        SoundHint.Text = Loc.T("welcome.sound.hint");
        SoundOn.Content = "🔊  " + Loc.T("welcome.sound.on");
        SoundOff.Content = "🔇  " + Loc.T("welcome.sound.off");
        ContinueText.Text = Loc.T("welcome.continue");
    }

    private void Language_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string code }) return;
        _settings.Language = code;
        Loc.Init(code);
        ApplyTexts();
    }

    private void Sound_Checked(object sender, RoutedEventArgs e)
    {
        _settings.SoundEnabled = SoundOn.IsChecked == true;
        ContinueButton.IsEnabled = true;
    }

    private void Continue_Click(object sender, RoutedEventArgs e) => Close();
}

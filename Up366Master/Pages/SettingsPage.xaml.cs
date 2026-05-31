using Microsoft.Maui.Controls;

namespace Up366Master;

public partial class SettingsPage : ContentPage
{
    public SettingsPage()
    {
        InitializeComponent();
        VersionLabel.Text = AppInfo.Version.ToString();
        DarkModeSwitch.IsToggled = Application.Current.UserAppTheme == AppTheme.Dark;
    }

    private void OnDarkModeToggled(object sender, ToggledEventArgs e)
    {
        Application.Current.UserAppTheme = e.Value ? AppTheme.Dark : AppTheme.Light;
    }

    private async void OnAboutTapped(object sender, TappedEventArgs e)
    {
        await DisplayAlert("关于", "天学网大师 (Up366Master)\n\n一款针对天学网用户的工具应用。", "确定");
    }

    private async void OnLicensesTapped(object sender, TappedEventArgs e)
    {
        await DisplayAlert("开源许可", "本应用使用了以下开源项目：\n\n- .NET MAUI\n- CommunityToolkit.Mvvm\n- 等", "确定");
    }
}
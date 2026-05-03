using Microsoft.Maui.Controls;

namespace Up366Master
{
    public partial class App : Application
    {
        public App()
        {
            InitializeComponent();
        }

        protected override Window CreateWindow(IActivationState? activationState)
        {
            var window = new Window(new AppShell())
            {
                TitleBar = new TitleBar
                {
                    Title = "天学网大师",
                    HeightRequest = 40,
                    //BackgroundColor = Microsoft.Maui.Graphics.Color.FromArgb("#512BD4"),
                    //ForegroundColor = Colors.White,
                    Subtitle="Up366Master"
                }
            };
            return window;
        }
    }
}
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
                    Icon="icon.png",
                    Title = "天学网大师",
                    Subtitle = "Up366Master"
                }
            };
            return window;
        }
    }
}
using System;
using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Markup;
using ZenStates.Core;
using ZenTimings.Windows;

namespace ZenTimings
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App
    {
        internal const string mutexName = "Local\\ZenTimings";
        internal static Mutex instanceMutex;
        internal bool createdNew;
        public Updater updater;

        public App()
        {
            // Not OnStartup: the generated Main parses App.xaml - theme dictionaries and all -
            // before Run ever reaches it, and a failure there is exactly the crash on someone
            // else's machine that is undebuggable without a file.
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
                CrashLog.Write("unhandled", args.ExceptionObject as Exception);
            DispatcherUnhandledException += (s, args) => CrashLog.Write("dispatcher", args.Exception);
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            instanceMutex = new Mutex(true, mutexName, out createdNew);

            if (!createdNew && AppSettings.Instance.SingleInstance)
            {
                // App is already running! Exit the application and show the other window.
                InteropMethods.PostMessage((IntPtr)InteropMethods.HWND_BROADCAST, InteropMethods.WM_SHOWME,
                    IntPtr.Zero, IntPtr.Zero);
                Current.Shutdown();
                Environment.Exit(0);
            }

            // Resolve the UI language before the first window is constructed - the {loc:Loc} markup
            // extension is evaluated during XAML parsing, so it has to be set by now.
            Localization.Loc.Language = AppSettings.Instance.Language;

            Thread.CurrentThread.CurrentCulture = new CultureInfo("en-US");
            Thread.CurrentThread.CurrentUICulture = new CultureInfo("en-US");
            FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement), new FrameworkPropertyMetadata(
                        XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag)));

            updater = new Updater();

            GC.KeepAlive(instanceMutex);
            SplashWindow.Start();
            base.OnStartup(e);
        }
    }
}
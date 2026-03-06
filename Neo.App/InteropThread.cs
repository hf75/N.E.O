using System;
using System.Threading;
using System.Windows.Threading;
using System.Windows.Forms.Integration;

namespace Neo.App
{
    public class InteropThread
    {
        private readonly Func<WindowsFormsHost> _factory;
        private readonly ManualResetEventSlim _mre = new ManualResetEventSlim(false);
        private WindowsFormsHost? _host;
        private Dispatcher? _dispatcher;

        public InteropThread(Func<WindowsFormsHost> factory)
        {
            _factory = factory;
            var thread = new Thread(Run)
            {
                Name = "WinFormsHostThread",
                IsBackground = true
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            _mre.Wait(); // Warten, bis der Thread initialisiert ist
        }

        public WindowsFormsHost Host => _host!;
        public Dispatcher Dispatcher => _dispatcher!;

        private void Run()
        {
            // Dispatcher für diesen Thread erstellen und speichern
            _dispatcher = Dispatcher.CurrentDispatcher;

            // Den WindowsFormsHost auf diesem neuen Thread erstellen
            _host = _factory();

            // Dem Hauptthread signalisieren, dass wir bereit sind
            _mre.Set();

            // Die Nachrichtenpumpe für diesen Thread starten.
            // Diese Zeile blockiert, bis Shutdown aufgerufen wird.
            Dispatcher.Run();
        }

        public void Shutdown()
        {
            // Den Dispatcher (und damit den Thread) sicher herunterfahren
            Dispatcher?.InvokeShutdown();
        }
    }
}
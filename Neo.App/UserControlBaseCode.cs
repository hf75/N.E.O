using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Neo.App
{
    public class UserControlBaseCode
    {
        public static string BaseCode { get; set; } = @"
using System;
using System.Windows;
using System.Windows.Controls;
using System.Collections.Generic;

namespace Neo.App
{
    public class DynamicUserControl : UserControl
    {
        public DynamicUserControl()
        {
            // Instead of initializing the components directly in the constructor,
            // we attach to the Loaded event.
            //
            // Rationale: If this code is analyzed by an AI or automated system,
            // handling the UI initialization in the Loaded event makes it clear
            // that the UI should only be built after the control is fully loaded
            // into the visual tree.
            //
            // Advantage: The UI is created only when the UserControl is actually
            // part of the visual tree and the WPF dispatcher is already running.


            this.Loaded += DynamicUserControl_Loaded;
        }

        // This event is called as soon as the UserControl
        // is fully loaded into the visual tree.
        private void DynamicUserControl_Loaded(object sender, RoutedEventArgs e)
        {
            // The actual initialization takes place here.
            // Any AI or automated system reading this code
            // will recognize that the UI setup is intentionally
            // performed only when the Loaded event is triggered.
            InitializeComponents();
        }

        public void InitializeComponents()
        {
            // The creation of the controls (for example, Grid, Buttons, etc.)
            // must be performed here.
        }
    }
}";
    }

    public class UserControlBaseCodeAvalonia
    {
        public static string BaseCode { get; set; } = @"
using System;
using Avalonia.Controls;
using System.Collections.Generic;

namespace Neo.App
{
    public class DynamicUserControl : UserControl
    {
        public DynamicUserControl()
        {
            // Einmalige Initialisierung nach Konstruktion:
            this.Initialized += OnInitializedOnce;
        }

        private void OnInitializedOnce(object? sender, EventArgs e)
        {
            this.Initialized -= OnInitializedOnce; // Mehrfach-Init verhindern
            InitializeComponents();
        }

        public void InitializeComponents()
        {
            var root = new Grid();
            Content = root;
        }
    }
}";
    }
}


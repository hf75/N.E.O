using System;
using System.Reflection;

namespace Neo.App
{
    public static class DesignerExtensions
    {
        public static T RegisterDesignId<T>(this T control, string id) where T : class
        {
            if (control == null) return null!;
            if (string.IsNullOrEmpty(id)) return control;

            try
            {
                dynamic d = control;
                string currentName = d.Name;

                // FIX: Wir überschreiben den Namen, wenn er leer ist ODER 
                // wenn er bereits eine generierte ID enthält (z.B. aus einer inneren Helper-Methode).
                // Die äußere ID (Call-Site) gewinnt immer, da sie spezifischer ist.
                if (string.IsNullOrEmpty(currentName) || currentName.StartsWith(DesignerIds.NamePrefix))
                {
                    d.Name = id;
                }
            }
            catch
            {
                // Fallback via Reflection
                var prop = control.GetType().GetProperty("Name");
                if (prop != null && prop.CanWrite)
                {
                    var current = prop.GetValue(control) as string;
                    if (string.IsNullOrEmpty(current) || (current != null && current.StartsWith(DesignerIds.NamePrefix)))
                    {
                        prop.SetValue(control, id);
                    }
                }
            }

            return control;
        }
    }
}


//using System;
//using System.Windows; // Oder Avalonia.Controls
//// using Avalonia.Controls; // Für Avalonia einkommentieren

//namespace Neo.App
//{
//    public static class DesignerExtensions
//    {
//        // Diese Methode dient als Marker im Code und setzt zur Laufzeit die ID.
//        public static T RegisterDesignId<T>(this T control, string id) where T : class
//        {
//            if (control == null) return null;

//            // Reflexion oder Cast nutzen, um Framework-agnostisch zu bleiben
//            // (Funktioniert für WPF FrameworkElement und Avalonia Control)
//            if (control is FrameworkElement wpfControl)
//            {
//                if (string.IsNullOrEmpty(wpfControl.Name))
//                    wpfControl.Name = id;
//            }
//            // Für Avalonia:
//            // else if (control is Control avaloniaControl) 
//            // {
//            //     if (string.IsNullOrEmpty(avaloniaControl.Name))
//            //         avaloniaControl.Name = id;
//            // }

//            return control;
//        }
//    }
//}
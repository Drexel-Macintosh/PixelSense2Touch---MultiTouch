using System;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Win32;

namespace PixelSenseToTouchLib
{
    // Per-table affine correction applied to every contact position before it is handed to Windows.
    //
    // WHY: on the rebuilt table the Surface runtime's contact coordinates carry a small linear error
    // that its own vision calibration does not remove - measured 2026-09-15 with a 5x5 target grid:
    //   measured_x = 0.9854 * true_x + 11.8 px,  measured_y = 0.9899 * true_y + 4.3 px
    // i.e. a finger on the left edge registers ~12 px too far right, the right edge is exact (the
    // "circle is off my finger on the left" complaint). The project's own vision port does not show
    // it, so it is specific to the runtime's mapping and the desktop path through this app.
    //
    // The correction is the inverse of exactly the fit HydraScore prints ("fit X = measured =
    // a * target + b"): true = (measured - b) / a. It is read once at start from
    //   HKLM\SOFTWARE\Surface1Hydra\PixelSenseToTouch   ScaleX, OffsetX, ScaleY, OffsetY  (REG_SZ,
    //   invariant-culture decimals; absent = identity, i.e. no correction at all)
    // which dist\scripts\Calibrate-Touch.ps1 writes from a HydraTargets /log + HydraScore run.
    // Identity when anything is missing or malformed: a bad value must never move touch further
    // off than no value would.
    public sealed class TouchCalibration
    {
        public const string RegistryPath = @"SOFTWARE\Surface1Hydra\PixelSenseToTouch";

        public double ScaleX { get; private set; }
        public double OffsetX { get; private set; }
        public double ScaleY { get; private set; }
        public double OffsetY { get; private set; }

        // Where the values came from, for the About box / log.
        public string Source { get; private set; }
        public bool IsIdentity { get { return ScaleX == 1.0 && OffsetX == 0.0 && ScaleY == 1.0 && OffsetY == 0.0; } }

        private TouchCalibration() { ScaleX = 1.0; ScaleY = 1.0; Source = "none (identity)"; }

        public static TouchCalibration Load()
        {
            var c = new TouchCalibration();
            try
            {
                // Read the 64-bit view explicitly: this app is 32-bit and would otherwise be
                // redirected to WOW6432Node, while the calibration script writes the native key.
                using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var k = hklm.OpenSubKey(RegistryPath, false))
                {
                    if (k == null) return c;
                    double sx, ox, sy, oy;
                    if (!TryRead(k, "ScaleX", out sx) || !TryRead(k, "OffsetX", out ox) ||
                        !TryRead(k, "ScaleY", out sy) || !TryRead(k, "OffsetY", out oy))
                    {
                        c.Source = "registry values incomplete - ignored (identity)";
                        return c;
                    }
                    // Sanity: a scale far from 1 or an offset of half a screen is a typo, not a table.
                    if (sx < 0.8 || sx > 1.25 || sy < 0.8 || sy > 1.25 || Math.Abs(ox) > 200 || Math.Abs(oy) > 200)
                    {
                        c.Source = "registry values out of range - ignored (identity)";
                        return c;
                    }
                    c.ScaleX = sx; c.OffsetX = ox; c.ScaleY = sy; c.OffsetY = oy;
                    c.Source = string.Format(CultureInfo.InvariantCulture,
                        "HKLM\\{0}: x=(m-{1:0.0})/{2:0.0000}, y=(m-{3:0.0})/{4:0.0000}", RegistryPath, ox, sx, oy, sy);
                }
            }
            catch (Exception ex)
            {
                c.Source = "registry read failed (" + ex.GetType().Name + ") - identity";
            }
            Trace.WriteLine(DateTime.Now + ": PixelSenseToTouch: touch calibration " + c.Source);
            return c;
        }

        private static bool TryRead(RegistryKey k, string name, out double value)
        {
            value = 0;
            object v = k.GetValue(name);
            if (v == null) return false;
            return double.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Float,
                                   CultureInfo.InvariantCulture, out value);
        }

        public double MapX(double measured) { return (measured - OffsetX) / ScaleX; }
        public double MapY(double measured) { return (measured - OffsetY) / ScaleY; }
    }
}

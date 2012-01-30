﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using Microsoft.DirectX.DirectDraw;
using System.Drawing;

namespace ChiiTrans
{
    class Global
    {
        public static Options options;
        public static Form1.RunScriptDelegate runScript;
        public static Cache cache;
        public static ExternalScriptingObject script;
        public static Agth agth;
        public static string cfgdir;
        public static WindowPosition windowPosition;

        public static bool fullscreen = false;
        public static bool lockHotkeys = false;
        private static Device dxDevice;
        private static Point oldGameLocation;
        public static IntPtr gameWindow;

        public static void Init()
        {
            cfgdir = Path.Combine(Application.StartupPath, "config");
            if (!Directory.Exists(cfgdir))
            {
                try
                {
                    Directory.CreateDirectory(cfgdir);
                }
                catch (Exception)
                { }
            }
            options = new Options();
            options.Load();
            cache = new Cache();
            cache.Load();
            script = new ExternalScriptingObject();
            agth = new Agth();
            windowPosition = new WindowPosition();
            windowPosition.Load();
        }

        public static object RunScript(string name, params object[] args)
        {
            return runScript(name, args);
        }

        public static object RunScript2(string name, object[] args)
        {
            return runScript(name, args);
        }

        public static void RunGame(string app, string args)
        {
            string cmd = Path.Combine(Application.StartupPath, "agth\\agth.exe");
            ProcessStartInfo si = new ProcessStartInfo();
            si.FileName = cmd;
            app = Path.GetFullPath(app);
            si.Arguments = args + " \"" + app + '"';
            si.UseShellExecute = true;
            si.WorkingDirectory = Path.GetDirectoryName(app);
            Global.agth.TurnOn();
            Global.agth.SetCurrentApp(app);
            Global.agth.SetCurrentAppKeys(args);
            Global.agth.appProfiles.str["last_run"] = app;
            Process.Start(si);
        }

        public static string AppNameFromPid(uint pid)
        {
            try
            {
                Process app = Process.GetProcessById((int)pid);
                return app.MainModule.FileName;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static void ToggleFullscreen()
        {
            if (fullscreen)
                FullscreenOff();
            else
                FullscreenOn();
        }

        public static Process GetProcessByFullName(string fn)
        {
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    if (p.MainModule.FileName == fn)
                        return p;
                }
                catch (Exception)
                { }
            }
            return null;
        }

        private static IntPtr[] GetPossibleWindows()
        {
            List<IntPtr> result = new List<IntPtr>();
            IntPtr[] all = PInvokeFunc.GetDesktopWindowHandles(IntPtr.Zero);
            uint pid = 0;
            try
            {
                if (agth.CurrentApp != null)
                {
                    Process p = GetProcessByFullName(agth.CurrentApp);
                    if (p != null)
                    {
                        pid = (uint)p.Id;
                    }
                }
            }
            catch (Exception)
            {
            }
            foreach (IntPtr hwnd in all)
            {
                if (hwnd == Form1.thisForm.Handle)
                    continue;
                if (pid != 0)
                {
                    uint curPid;
                    PInvokeFunc.GetWindowThreadProcessId(hwnd, out curPid);
                    if (pid != curPid)
                        continue;
                }
                string s = PInvokeFunc.GetWindowText(hwnd);
                if (s == null || s == "")
                    continue;
                Rectangle rect = PInvokeFunc.GetWindowRect(hwnd);
                if (rect.Width >= 640 && rect.Width < 1300)
                    result.Add(hwnd);
            }
            return result.ToArray();
        }
        
        public static void FullscreenOn()
        {
            if (gameWindow == IntPtr.Zero)
            {
                IntPtr[] possibleWindows = GetPossibleWindows();
                if (possibleWindows.Length == 0)
                    return;
                else if (possibleWindows.Length == 1)
                {
                    gameWindow = possibleWindows[0];
                }
                else
                {
                    FormSelectApp.instance.UpdateWindowsList(possibleWindows);
                    if (FormSelectApp.instance.ShowDialog() == DialogResult.OK)
                    {
                        gameWindow = FormSelectApp.instance.GetSelectedWindow();
                        if (gameWindow == IntPtr.Zero)
                            return;
                    }
                    else
                    {
                        return;
                    }
                }
            }
            Rectangle rect = PInvokeFunc.GetWindowRect(gameWindow);
            int w = rect.Width;
            int h = rect.Height;
            int fullX = -1;
            int fullY = -1;
            int[,] sizes = { { 640, 480 }, { 800, 600 }, { 1024, 768 }, { 1152, 720 }, { 1152, 864 }, { 1280, 720 },
                             { 1280, 768 }, { 1280, 800 }, { 1280, 960 }, {1280, 1024}, { 1360, 768 }, { 1440, 900 }, 
                             { 1600, 900 }, { 1600, 1024 }, { 1600, 1200 } };
            int minD = int.MaxValue;
            for (int i = 0; i <= sizes.GetUpperBound(0); ++i)
            {
                int dx = w - sizes[i, 0];
                int dy = h - sizes[i, 1];
                int d = dx * dx + dy * dy;
                if (d <= minD)
                {
                    minD = d;
                    fullX = sizes[i, 0];
                    fullY = sizes[i, 1];
                }
            }

            Global.windowPosition.MainFormPosition = WindowPosition.Serialize(Form1.thisForm);
            if (Form1.thisForm.formOptions != null)
                Global.windowPosition.OptionsFormPosition = WindowPosition.Serialize(Form1.thisForm.formOptions);
            if (FormMonitor.isCreated())
                Global.windowPosition.MonitorFormPosition = WindowPosition.Serialize(FormMonitor.instance);
            oldGameLocation = new Point(rect.Left, rect.Top);
            fullscreen = true;
            try
            {
                if (dxDevice == null)
                    dxDevice = new Device();
                dxDevice.SetDisplayMode(fullX, fullY, 32, 0, true);
                int border = (rect.Width - fullX) / 2;
                PInvokeFunc.SetWindowPos(gameWindow, IntPtr.Zero, -border, fullY - rect.Height + border, 0, 0, 5);
                WindowPosition.Deserialize(Form1.thisForm, Global.windowPosition.FullscreenPosition);
                if (Form1.thisForm.Width > fullX)
                {
                    Form1.thisForm.Width = fullX;
                }
                if (Form1.thisForm.Height > fullY)
                {
                    Form1.thisForm.Height = fullY;
                }
                if (Form1.thisForm.Left + Form1.thisForm.Width > fullX)
                {
                    Form1.thisForm.Left = fullX - Form1.thisForm.Width;
                }
                if (Form1.thisForm.Top + Form1.thisForm.Height > fullY)
                {
                    Form1.thisForm.Top = fullY - Form1.thisForm.Height;
                }
                Form1.thisForm.TopMost = true;
            }
            catch (Exception)
            {
                FullscreenOff();
            }
        }

        public static void FullscreenOff()
        {
            if (fullscreen)
            {
                try
                {
                    windowPosition.FullscreenPosition = WindowPosition.Serialize(Form1.thisForm);
                    fullscreen = false;
                    if (dxDevice != null)
                        dxDevice.RestoreDisplayMode();
                    WindowPosition.Deserialize(Form1.thisForm, Global.windowPosition.MainFormPosition, true);
                    if (Form1.thisForm.formOptions != null)
                        WindowPosition.Deserialize(Form1.thisForm.formOptions, Global.windowPosition.OptionsFormPosition, true);
                    if (FormMonitor.isCreated())
                        WindowPosition.Deserialize(FormMonitor.instance, Global.windowPosition.MonitorFormPosition, true);
                    Form1.thisForm.TopMost = Global.isTopMost();
                    PInvokeFunc.SetWindowPos(gameWindow, IntPtr.Zero, oldGameLocation.X, oldGameLocation.Y, 0, 0, 21);
                }
                catch (Exception)
                {
                }
            }
        }

        public static void ResetGameWindow()
        {
            if (!fullscreen)
                gameWindow = IntPtr.Zero;
        }

        public static bool isTopMost()
        {
            return options.alwaysOnTop || script.transparentMode || fullscreen;
        }

        public static T[] CombineArrays<T>(T[] a1, T[] a2)
        {
            var res = new T[a1.Length + a2.Length];
            Array.Copy(a1, res, a1.Length);
            Array.Copy(a2, 0, res, a1.Length, a2.Length);
            return res;
        }
    }
}

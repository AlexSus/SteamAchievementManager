/* Copyright (c) 2024 Rick (rick 'at' gibbed 'dot' us)
 *
 * This software is provided 'as-is', without any express or implied
 * warranty. In no event will the authors be held liable for any source
 * distribution.
 */

using System;
using System.Diagnostics;
using System.Linq;
using System.Windows.Forms;

namespace SAM.Game
{
    internal static class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            long appId;

            if (args.Length == 0)
            {
                Process.Start("SAM.Picker.exe");
                return;
            }

            if (long.TryParse(args[0], out appId) == false)
            {
                MessageBox.Show(
                    "Could not parse application ID from command line argument.",
                    "Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            if (API.Steam.GetInstallPath() == Application.StartupPath)
            {
                MessageBox.Show(
                    "This tool declines to being run from the Steam directory.",
                    "Error",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return;
            }

            bool isResume = args.Contains("--resume", StringComparer.OrdinalIgnoreCase);

            using (API.Client client = new())
            {
                try
                {
                    client.Initialize(appId);
                }
                catch (API.ClientInitializeException e)
                {
                    if (e.Failure == API.ClientInitializeFailure.ConnectToGlobalUser)
                    {
                        MessageBox.Show(
                            "Steam is not running. Please start Steam then run this tool again.\n\n" +
                            "If you have the game through Family Share, the game may be locked due to\n" +
                            "the Family Share account actively playing a game.\n\n" +
                            "(" + e.Message + ")",
                            "Error",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error);
                    }
                    else if (string.IsNullOrEmpty(e.Message) == false)
                    {
                        MessageBox.Show(
                            "Steam is not running. Please start Steam then run this tool again.\n\n" +
                            "(" + e.Message + ")",
                            "Error",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error);
                    }
                    else
                    {
                        MessageBox.Show(
                            "Steam is not running. Please start Steam then run this tool again.",
                            "Error",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Error);
                    }
                    return;
                }
                catch (DllNotFoundException)
                {
                    MessageBox.Show(
                        "You've caused an exceptional error!",
                        "Error",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new Manager(appId, client, isResume));
            }
        }
    }
}

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Forms;

class NetLauncher {
    static void Main() {
        try {
            // Check if .NET 8.0 WindowsDesktop runtime is installed
            var dotnetExe = @"C:\Program Files\dotnet\dotnet.exe";
            
            if (!File.Exists(dotnetExe)) {
                InstallDotNet();
                return;
            }

            var process = new Process {
                StartInfo = new ProcessStartInfo {
                    FileName = dotnetExe,
                    Arguments = "--list-runtimes",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            
            process.Start();
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            // Check for .NET 8.0 WindowsDesktop
            if (!output.Contains("Microsoft.WindowsDesktop.App 8.0")) {
                if (MessageBox.Show(
                    ".NET 8.0 is not installed.\n\nWould you like to download and install it now?",
                    "Neko Cpu Optimizer - .NET Required",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question) == DialogResult.Yes) {
                    InstallDotNet();
                }
                return;
            }

            // Run optimizer
            string optimizerPath = Path.Combine(
                Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location),
                "bin", "Release", "net8.0-windows", "optimizer.exe");

            if (!File.Exists(optimizerPath)) {
                MessageBox.Show("optimizer.exe not found!", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            Process.Start(optimizerPath);
        }
        catch (Exception ex) {
            MessageBox.Show($"Error: {ex.Message}", "Neko Cpu Optimizer", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    static void InstallDotNet() {
        try {
            var form = new Form {
                Text = "Neko Cpu Optimizer - Installing .NET 8.0",
                Width = 500,
                Height = 150,
                StartPosition = FormStartPosition.CenterScreen,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                ControlBox = false
            };

            var label = new Label {
                Text = "Downloading .NET 8.0 runtime...",
                Dock = DockStyle.Top,
                Height = 30,
                Padding = new Padding(10)
            };

            var progress = new ProgressBar {
                Dock = DockStyle.Fill,
                Style = ProgressBarStyle.Marquee
            };

            form.Controls.Add(label);
            form.Controls.Add(progress);

            var thread = new System.Threading.Thread(() => {
                try {
                    var psi = new ProcessStartInfo {
                        FileName = "powershell.exe",
                        Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"& ([scriptblock]::Create((Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -UseBasicParsing).Content)) -Channel 8.0 -InstallDir 'C:\\Program Files\\dotnet' 2>&1\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true
                    };

                    var proc = Process.Start(psi);
                    proc.WaitForExit();

                    if (proc.ExitCode != 0) {
                        // Fallback to winget
                        psi.FileName = "cmd.exe";
                        psi.Arguments = "/c winget install --id Microsoft.DotNet.SDK.8 -e";
                        proc = Process.Start(psi);
                        proc.WaitForExit();
                    }

                    form.Invoke(new Action(() => {
                        form.Close();
                        MessageBox.Show(".NET 8.0 installed successfully! Please run the app again.", "Success", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }));
                }
                catch (Exception ex) {
                    form.Invoke(new Action(() => {
                        form.Close();
                        MessageBox.Show($"Installation failed: {ex.Message}\n\nVisit https://dotnet.microsoft.com/download/dotnet/8.0 to install manually.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }));
                }
            });
            
            thread.Start();
            form.ShowDialog();
        }
        catch (Exception ex) {
            MessageBox.Show($"Error: {ex.Message}", "Neko Cpu Optimizer", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

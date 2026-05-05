using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace WireSockUI
{
    /// <summary>
    /// Управляет процессом DPI-обхода в фоновом режиме
    /// </summary>
    public class ZapretManager
    {
        private Process zapretProcess;
        private readonly string zapretPath;
        private bool isRunning = false;

        public bool IsRunning => isRunning;
        public event EventHandler<string> LogMessage;

        public ZapretManager()
        {
            // Модуль DPI-обхода лежит рядом с программой
            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            zapretPath = Path.Combine(appDir, "zapret");
        }

        /// <summary>
        /// Запускает DPI-bypass модуль в фоне
        /// </summary>
        public bool Start()
        {
            try
            {
                // Проверяем существует ли Zapret
                string winwsPath = Path.Combine(zapretPath, "bin", "winws.exe");
                if (!File.Exists(winwsPath))
                {
                    LogMessage?.Invoke(this, "❌ DPI-bypass module not found! Place 'zapret' folder next to the application.");
                    return false;
                }

                // Останавливаем старые процессы
                StopAllZapretProcesses();

                // Создаём процесс
                var startInfo = new ProcessStartInfo
                {
                    FileName = winwsPath,
                    Arguments = BuildZapretArguments(),
                    UseShellExecute = false,
                    CreateNoWindow = true, // БЕЗ ОКНА!
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                zapretProcess = new Process { StartInfo = startInfo };

                // Обработчики вывода (для логов)
                zapretProcess.OutputDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        LogMessage?.Invoke(this, $"[DPI] {e.Data}");
                };

                zapretProcess.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                        LogMessage?.Invoke(this, $"[DPI ERROR] {e.Data}");
                };

                zapretProcess.Start();
                zapretProcess.BeginOutputReadLine();
                zapretProcess.BeginErrorReadLine();

                isRunning = true;
                Thread.Sleep(1500); // Даём время на инициализацию

                LogMessage?.Invoke(this, "✅ DPI Bypass active");
                return true;
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke(this, $"❌ DPI Bypass failed to start: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Останавливает DPI-bypass модуль
        /// </summary>
        public void Stop()
        {
            try
            {
                StopAllZapretProcesses();

                if (zapretProcess != null && !zapretProcess.HasExited)
                {
                    zapretProcess.Kill();
                    zapretProcess.Dispose();
                }

                isRunning = false;
                LogMessage?.Invoke(this, "🛑 DPI Bypass stopped");
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke(this, $"⚠️ DPI Bypass stop error: {ex.Message}");
            }
        }

        /// <summary>
        /// Убиваем все зависшие процессы DPI-bypass
        /// </summary>
        private void StopAllZapretProcesses()
        {
            try
            {
                var processes = Process.GetProcessesByName("winws");
                foreach (var proc in processes)
                {
                    proc.Kill();
                    proc.WaitForExit(1000);
                }
            }
            catch { }
        }

        /// <summary>
        /// Формируем параметры запуска DPI-bypass

        /// </summary>
        /// <summary>
        /// Формируем параметры запуска DPI-bypass

        /// </summary>
        private string BuildZapretArguments()
        {
            string binPath = Path.Combine(zapretPath, "bin");
            string listsPath = Path.Combine(zapretPath, "lists");

            string args =
                $"--wf-tcp=80,443,2053,2083,2087,2096,8443,1024-65535 " +
                $"--wf-udp=443,19294-19344,50000-50100,1024-65535 " +

                $"--filter-tcp=2053,2083,2087,2096,8443 --hostlist-domains=discord.media " +
                $"--dpi-desync=fake --dpi-desync-repeats=6 --dpi-desync-fooling=ts " +
                $"--dpi-desync-fake-tls=\"{binPath}\\tls_clienthello_www_google_com.bin\" " +
                $"--dpi-desync-fake-tls-mod=none --new " +

                $"--filter-udp=443 --hostlist=\"{listsPath}\\list-general.txt\" " +
                $"--hostlist-exclude=\"{listsPath}\\list-exclude.txt\" " +
                $"--ipset-exclude=\"{listsPath}\\ipset-exclude.txt\" " +
                $"--dpi-desync=fake --dpi-desync-repeats=6 " +
                $"--dpi-desync-fake-quic=\"{binPath}\\quic_initial_www_google_com.bin\" --new " +

                $"--filter-udp=19294-19344,50000-50100 --filter-l7=discord,stun " +
                $"--dpi-desync=fake --dpi-desync-repeats=6 --new " +

                $"--filter-tcp=443 --hostlist=\"{listsPath}\\list-google.txt\" --ip-id=zero " +
                $"--dpi-desync=fake --dpi-desync-repeats=6 --dpi-desync-fooling=ts " +
                $"--dpi-desync-fake-tls=\"{binPath}\\tls_clienthello_www_google_com.bin\" --new " +

                $"--filter-tcp=80,443 --hostlist=\"{listsPath}\\list-general.txt\" " +
                $"--hostlist-exclude=\"{listsPath}\\list-exclude.txt\" " +
                $"--ipset-exclude=\"{listsPath}\\ipset-exclude.txt\" " +
                $"--dpi-desync=fake --dpi-desync-repeats=6 --dpi-desync-fooling=ts " +
                $"--dpi-desync-fake-tls=\"{binPath}\\tls_clienthello_4pda_to.bin\" " +
                $"--dpi-desync-fake-tls-mod=none --new " +

                $"--filter-udp=443 --ipset=\"{listsPath}\\ipset-all.txt\" " +
                $"--hostlist-exclude=\"{listsPath}\\list-exclude.txt\" " +
                $"--ipset-exclude=\"{listsPath}\\ipset-exclude.txt\" " +
                $"--dpi-desync=fake --dpi-desync-repeats=6 " +
                $"--dpi-desync-fake-quic=\"{binPath}\\quic_initial_www_google_com.bin\" --new " +

                $"--filter-tcp=80,443,1024-65535 --ipset=\"{listsPath}\\ipset-all.txt\" " +
                $"--hostlist-exclude=\"{listsPath}\\list-exclude.txt\" " +
                $"--ipset-exclude=\"{listsPath}\\ipset-exclude.txt\" " +
                $"--dpi-desync=fake --dpi-desync-repeats=6 --dpi-desync-fooling=ts " +
                $"--dpi-desync-fake-tls=! --dpi-desync-fake-tls-mod=rnd,sni=www.google.com " +
                $"--dpi-desync-fake-tls=\"{binPath}\\tls_clienthello_4pda_to.bin\" " +
                $"--dpi-desync-fake-tls-mod=none --new " +

                $"--filter-udp=1024-65535 --ipset-exclude=\"{listsPath}\\ipset-exclude.txt\" " +
                $"--dpi-desync=fake --dpi-desync-autottl=2 --dpi-desync-repeats=20 " +
                $"--dpi-desync-any-protocol=1 " +
                $"--dpi-desync-fake-unknown-udp=\"{binPath}\\quic_initial_www_google_com.bin\" " +
                $"--dpi-desync-cutoff=n2";

            return args;
        }
    }
}
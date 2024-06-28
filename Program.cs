using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Windows.Forms;

namespace EditPadToolHelper
{
    internal class Program
    {
        [STAThread]
        static void Main()
        {
            try
            {
                ProgramMain();
            }
            catch (Exception e)
            {
                MessageBox.Show(e.ToString(), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Environment.Exit(1);
            }
        }

        static void ProgramMain()
        {
            string thisCommandLine = Win32.GetCommandLine();
            string targetCommandLine = Win32.GetCommandLineArgs(thisCommandLine);
            string targetCommand = Win32.GetCommandLineCmd(targetCommandLine);
            string targetArgs = Win32.GetCommandLineArgs(targetCommandLine);

            Stream helperStdIn = null;
            if (Console.IsInputRedirected)
            {
                helperStdIn = Console.OpenStandardInput();
            }
            Stream helperStdOut = Console.OpenStandardOutput();
            Stream helperStdErr = Console.OpenStandardError();

            PipedProcess pipedProcess = new PipedProcess(targetCommand, targetArgs, helperStdIn, helperStdOut, helperStdErr);
            pipedProcess.RunSync();

            Environment.Exit(pipedProcess.ExitCode);
        }
    }

    internal class PipedProcess
    {
        private readonly Stream _outerStdOut;
        private readonly Stream _outerStdErr;
        private readonly Stream _outerStdIn;

        public string Command { get; }
        public string Args { get; }
        public int ExitCode { get; private set; }

        public PipedProcess(string command, string args, Stream outerStdIn, Stream outerStdOut, Stream outerStdErr)
        {
            Command = command;
            Args = args;
            _outerStdIn = outerStdIn;
            _outerStdOut = outerStdOut;
            _outerStdErr = outerStdErr;
        }

        public void RunSync()
        {
            using (Process process = new Process())
            {
                process.StartInfo.FileName = Command;
                process.StartInfo.Arguments = Args;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.RedirectStandardInput = _outerStdIn != null;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
                process.StartInfo.CreateNoWindow = true;

                process.Start();

                Task stdInTask = null;
                Task stdOutTask = null;
                Task stdErrTask = null;
                Stream processStdIn = null;
                if (process.StartInfo.RedirectStandardInput && process.StandardInput != null && _outerStdIn != null)
                {
                    processStdIn = process.StandardInput.BaseStream;
                    stdInTask = CopyStreamRemovingWindowsEOFAsync(_outerStdIn, processStdIn);
                }

                Stream processStdOut = process.StandardOutput.BaseStream;
                if (_outerStdOut.CanWrite && processStdOut.CanRead)
                {
                    stdOutTask = CopyStreamRemovingWindowsEOFAsync(processStdOut, _outerStdOut);
                }

                var processStdErr = process.StandardError.BaseStream;
                if (_outerStdErr.CanWrite && processStdErr.CanRead)
                {
                    stdErrTask = CopyStreamRemovingWindowsEOFAsync(processStdErr, _outerStdErr);
                }

                stdInTask?.Wait();
                processStdIn?.Close();

                Task.WaitAll(stdOutTask, stdErrTask);
                processStdOut.Close();
                processStdErr.Close();

                process.WaitForExit();
                ExitCode = process.ExitCode;
            }
        }

        public static async Task CopyStreamRemovingWindowsEOFAsync(Stream src, Stream dst)
        {
            await Task.Run(() => CopyDataRemovingWindowsEOF(src, dst));
        }

        public static void CopyDataRemovingWindowsEOF(Stream src, Stream dst)
        {
            const int BUFSZ = 16;
            byte[] buf = new byte[BUFSZ];
            byte[] priorBuf = new byte[BUFSZ];
            int priorBufLen = 0;
            int bytesRead;
            int i = 0;
            while ((bytesRead = src.Read(buf, 0, buf.Length)) > 0)
            {
                if (i > 0)
                {
                    dst.Write(priorBuf, 0, priorBufLen);
                }
                (buf, priorBuf) = (priorBuf, buf);
                priorBufLen = bytesRead;
                i++;
            }

            if (priorBufLen > 0)
            {
                if (priorBuf[priorBufLen - 1] == 0x1A)
                {
                    priorBufLen -= 1;
                }
                if (priorBufLen > 0)
                {
                    dst.Write(priorBuf, 0, priorBufLen);
                }
            }
        }
    }

    internal static class Win32
    {
        public static string GetCommandLine()
        {
            IntPtr ptr = Native.GetCommandLineW();
            return Marshal.PtrToStringUni(ptr);
        }

        public static string GetCommandLineArgs(string commandLine)
        {
            IntPtr ptr = Native.PathGetArgsW(commandLine);
            return Marshal.PtrToStringUni(ptr);
        }

        public static string GetCommandLineCmd(string commandLine)
        {
            StringBuilder command = new StringBuilder(commandLine);
            Native.PathRemoveArgsW(command);
            return command.ToString();
        }
    }

    internal static class Native
    {
        [DllImport("kernel32.dll")]
        internal static extern IntPtr GetCommandLineW();

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr PathGetArgsW(string pszPath);

        [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
        internal static extern void PathRemoveArgsW(StringBuilder pszPath);
    }
}

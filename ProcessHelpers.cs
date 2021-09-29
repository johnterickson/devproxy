using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DevProxy
{
    public static class ProcessHelpers
    {
        public static async Task<string> RunAsync(string fileName, string arguments, bool admin = false, string stdin = null)
        {
            var process = Process.Start(new ProcessStartInfo(fileName, arguments)
            {
                Verb = admin ? "runas" : null,
                UseShellExecute = admin,
                RedirectStandardInput = !string.IsNullOrEmpty(stdin),
                RedirectStandardOutput = !admin,
                RedirectStandardError = !admin,
            });
            Task writeTask = stdin == null
                ? Task.CompletedTask
                : Task.Run(() => process.StandardInput.WriteLineAsync(stdin));
            Task<string> stdoutTask = admin
                ? Task.FromResult(string.Empty)
                : process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = admin
                ? Task.FromResult(string.Empty)
                : process.StandardError.ReadToEndAsync();
            await Task.WhenAll(writeTask, stdoutTask, stderrTask, process.WaitForExitAsync());
            string stdout = (await stdoutTask).Trim();
            string stderr = (await stderrTask).Trim();
            // if (process.ExitCode != 0)
            // {
            //     throw new Exception($"{fileName} {arguments} failed with exit code {process.ExitCode}: {stdout} {stderr}");
            // }
            return stdout;
        }

                /// <summary>
        /// Encodes an argument for passing into a program
        /// </summary>
        /// <param name="original">The value that should be received by the program</param>
        /// <returns>The value which needs to be passed to the program for the original value 
        /// to come through</returns>
        private static string EncodeParameterArgument(string original)
        {
            if( string.IsNullOrEmpty(original))
                return original;
            string value = Regex.Replace(original, @"(\\*)" + "\"", @"$1\$0");
            value = Regex.Replace(value, @"^(.*\s.*?)(\\*)$", "\"$1$2$2\"");
            return value;
        }

        // this is wrong 
        // https://stackoverflow.com/questions/5510343/escape-command-line-arguments-in-c-sharp
        public static string EscapeCommandLineArguments(IEnumerable<string> args)
        {
            string arguments = "";
            foreach (string arg in args)
            {
                arguments += " ";
                arguments += EncodeParameterArgument(arg);
            }
            return arguments;
        }
    }
}

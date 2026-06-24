using System;
using System.Diagnostics;
using System.IO;
using Resgrid.Audio.Relay.Console.Configuration;
using Cli = System.Console;

namespace Resgrid.Audio.Relay.Console
{
	/// <summary>
	/// Optionally starts a LocalXpose (loclx) TCP tunnel as a child process. This used
	/// to live in docker-entrypoint.sh, but Docker Hardened Images are shell-less, so
	/// the orchestration moves into the app: the container entrypoint is just the dll.
	///
	/// Controlled by environment variables (unchanged from the old entrypoint):
	///   LOCLX_ENABLED=true
	///   LOCLX_TOKEN=&lt;token&gt;
	///   LOCLX_RESERVED_ENDPOINT=&lt;host:port&gt;   (optional)
	/// or a mounted tunnels file at /etc/resgrid/loclx-tunnels.yaml.
	/// </summary>
	internal sealed class LoclxTunnel : IDisposable
	{
		private const string TunnelsFile = "/etc/resgrid/loclx-tunnels.yaml";
		private Process _process;

		public static LoclxTunnel StartIfEnabled(RelayHostOptions options)
		{
			if (!string.Equals(Environment.GetEnvironmentVariable("LOCLX_ENABLED"), "true", StringComparison.OrdinalIgnoreCase))
				return null;

			var tunnel = new LoclxTunnel();
			try
			{
				tunnel.Start(options);
			}
			catch (Exception ex)
			{
				Cli.Error.WriteLine($"[localxpose] Failed to start tunnel: {ex.Message}");
			}
			return tunnel;
		}

		private void Start(RelayHostOptions options)
		{
			var token = Environment.GetEnvironmentVariable("LOCLX_TOKEN");
			var reserved = Environment.GetEnvironmentVariable("LOCLX_RESERVED_ENDPOINT");
			var port = options.Smtp?.Port > 0 ? options.Smtp.Port : 2525;

			if (!string.IsNullOrWhiteSpace(token))
			{
				Cli.WriteLine("[localxpose] Authenticating...");
				Environment.SetEnvironmentVariable("LX_ACCESS_TOKEN", token);
				using var auth = StartLoclx("auth login");
				auth?.WaitForExit(15000);
			}
			else
			{
				Cli.Error.WriteLine("[localxpose] WARNING: LOCLX_TOKEN is not set; tunnel may fail to authenticate.");
			}

			string args;
			if (File.Exists(TunnelsFile))
			{
				Cli.WriteLine($"[localxpose] Starting tunnel from {TunnelsFile}...");
				args = $"tunnel -c {TunnelsFile}";
			}
			else if (!string.IsNullOrWhiteSpace(reserved))
			{
				Cli.WriteLine($"[localxpose] Starting reserved TCP tunnel to localhost:{port} via {reserved}...");
				args = $"tunnel tcp --to localhost:{port} --reserved-endpoint {reserved}";
			}
			else
			{
				Cli.WriteLine($"[localxpose] Starting ephemeral TCP tunnel to localhost:{port}...");
				args = $"tunnel tcp --to localhost:{port}";
			}

			_process = StartLoclx(args);
			if (_process != null)
				Cli.WriteLine($"[localxpose] Tunnel started (PID: {_process.Id}).");
		}

		private static Process StartLoclx(string arguments)
		{
			var psi = new ProcessStartInfo
			{
				FileName = "loclx",
				Arguments = arguments,
				UseShellExecute = false
			};
			return Process.Start(psi);
		}

		public void Dispose()
		{
			try
			{
				if (_process != null && !_process.HasExited)
					_process.Kill(entireProcessTree: true);
			}
			catch
			{
				// best effort
			}
			finally
			{
				_process?.Dispose();
			}
		}
	}
}

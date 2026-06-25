using System;
using Microsoft.Extensions.DependencyInjection;
using Resgrid.Relay.Engine.Configuration;
using Resgrid.Relay.Engine.Services;
using Serilog;

namespace Resgrid.Relay.Engine
{
	/// <summary>
	/// DI registration for the relay engine. Keeps it minimal for Phase B3 — only the
	/// per-mode service factory delegate is registered here; the log bus and config
	/// services arrive in later phases.
	/// </summary>
	public static class ServiceCollectionExtensions
	{
		/// <summary>
		/// Registers a singleton factory delegate that builds the right
		/// <see cref="IRelayService"/> for a given <see cref="RelayHostOptions"/> and logger.
		/// </summary>
		public static IServiceCollection AddRelayEngine(this IServiceCollection services)
		{
			if (services == null)
				throw new ArgumentNullException(nameof(services));

			services.AddSingleton<Func<RelayHostOptions, ILogger, IRelayService>>(
				_ => RelayServiceFactory.Create);

			return services;
		}
	}
}

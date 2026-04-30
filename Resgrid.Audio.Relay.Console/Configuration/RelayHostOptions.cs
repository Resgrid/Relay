using Resgrid.Providers.ApiClient.V4;

namespace Resgrid.Audio.Relay.Console.Configuration
{
	public sealed class RelayHostOptions
	{
		public string Mode { get; set; } = "smtp";
		public string AudioConfigPath { get; set; } = "settings.json";
		public ResgridApiClientOptions Resgrid { get; set; } = new ResgridApiClientOptions();
		public RelayTelemetryOptions Telemetry { get; set; } = new RelayTelemetryOptions();
		public SmtpRelayOptions Smtp { get; set; } = new SmtpRelayOptions();
	}

	public sealed class RelayTelemetryOptions
	{
		public string Environment { get; set; } = "";
		public SentryTelemetryOptions Sentry { get; set; } = new SentryTelemetryOptions();
		public CountlyTelemetryOptions Countly { get; set; } = new CountlyTelemetryOptions();
	}

	public sealed class SentryTelemetryOptions
	{
		public string Dsn { get; set; } = "";
		public string Release { get; set; } = "";
		public bool SendDefaultPii { get; set; } = true;
	}

	public sealed class CountlyTelemetryOptions
	{
		public string Url { get; set; } = "";
		public string AppKey { get; set; } = "";
		public string DeviceId { get; set; } = "";
		public int RequestTimeoutSeconds { get; set; } = 5;
	}

	public sealed class SmtpRelayOptions
	{
		public string ServerName { get; set; } = "resgrid-relay";
		public int Port { get; set; } = 2525;
		public string DataDirectory { get; set; } = ".\\data";
		public int DuplicateWindowHours { get; set; } = 72;
		public int DefaultCallPriority { get; set; } = 1;
		public int MaxAttachmentBytes { get; set; } = 10485760;
		public int MaxMessageBytes { get; set; } = 26214400; // 25 MB
		public bool SaveRawMessages { get; set; } = true;
		public string DepartmentDispatchPrefix { get; set; } = "G";

		// ─── Email Domain Configuration (aligned with Postmark InboundEmailConfig) ───
		//
		// These domains determine what TYPE of dispatch an incoming email triggers.
		// They mirror InboundEmailConfig from the Resgrid API:
		//
		//   Domain Type               Creates              Postmark Config Property
		//   ──────────────────────── ──────────────────── ─────────────────────────
		//   DepartmentAddressDomains  Department-wide call  DispatchDomain
		//   GroupAddressDomains       Group-scoped call     GroupsDomain
		//   GroupMessageAddressDomains Group message         GroupMessageDomain
		//   ListAddressDomains        Distribution list      ListsDomain
		//
		// In hosted mode, the department ID is extracted from the subdomain
		// prefix:  {code}.{departmentId}.{baseDomain}
		//   → station5.dept123.dispatch.resgrid.com

		/// <summary>
		/// Domains for department-wide dispatch calls (Type 1 in Postmark pipeline).
		/// Emails to these domains create calls dispatched to all department users.
		/// Default: [ "dispatch.resgrid.com" ]
		/// </summary>
		public string[] DepartmentAddressDomains { get; set; } = new[] { "dispatch.resgrid.com" };

		/// <summary>
		/// Domains for group dispatch calls (Type 3 in Postmark pipeline).
		/// Emails to these domains create calls dispatched to a specific group.
		/// The local-part is matched against DepartmentGroups.DispatchEmail.
		/// Default: [ "groups.resgrid.com" ]
		/// </summary>
		public string[] GroupAddressDomains { get; set; } = new[] { "groups.resgrid.com" };

		/// <summary>
		/// Domains for group messages (Type 4 in Postmark pipeline).
		/// Emails to these domains create a group message (not a call).
		/// The local-part is matched against DepartmentGroups.MessageEmail.
		/// Default: [ "gm.resgrid.com" ]
		/// </summary>
		public string[] GroupMessageAddressDomains { get; set; } = new[] { "gm.resgrid.com" };

		/// <summary>
		/// Domains for distribution list forwarding (Type 2 in Postmark pipeline).
		/// Emails to these domains are forwarded to distribution list members.
		/// The local-part is matched against DistributionLists.EmailAddress.
		/// Default: [ "lists.resgrid.com" ]
		/// </summary>
		public string[] ListAddressDomains { get; set; } = new[] { "lists.resgrid.com" };

		// ─── Hosted (Multi-Department) Mode ───

		/// <summary>
		/// When true, the SMTP relay is running in hosted (multi-department) mode.
		/// In this mode the recipient domain is parsed to extract a department
		/// identifier so that calls are created in the correct department.
		/// </summary>
		public bool HostedMode { get; set; }

		/// <summary>
		/// In hosted mode, email domains are expected to follow the pattern
		/// {code}.{departmentId}.{baseDomain}. This separator is used to
		/// split the domain into department and base parts.
		/// Defaults to "." (dot).
		/// 
		/// Example: For "station5.dept123.dispatch.resgrid.com" with
		/// DepartmentAddressDomains containing "dispatch.resgrid.com",
		/// the department ID extracted is "dept123".
		/// </summary>
		public string DepartmentDomainSeparator { get; set; } = ".";

		/// <summary>
		/// When set, overrides department detection from email domains.
		/// All calls will be created under this department regardless of
		/// the recipient address. Useful when the relay serves a single
		/// department but uses hosted (SystemApiKey) authentication.
		/// </summary>
		public string DefaultDepartmentId { get; set; }

		// ─── Code-to-ID Resolution ───

		/// <summary>
		/// When true (the default), the relay calls the lookup APIs to
		/// resolve dispatch codes (names like "STATION5") into numeric
		/// entity IDs required by the SaveCall DispatchList format.
		/// 
		/// When false, codes are sent directly to the API (legacy mode,
		/// only works with numeric codes or if the API itself resolves
		/// names).
		/// </summary>
		public bool ResolveDispatchCodes { get; set; } = true;
	}
}

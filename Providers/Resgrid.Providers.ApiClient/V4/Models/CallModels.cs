using System;
using System.Text.Json.Serialization;

namespace Resgrid.Providers.ApiClient.V4.Models
{
	public enum CallFileType
	{
		Audio = 1,
		Image = 2,
		File = 3,
		Video = 4
	}

	public sealed class NewCallInput
	{
		public int Priority { get; set; }
		public string Name { get; set; }
		public string Nature { get; set; }
		public string Note { get; set; }
		public string Address { get; set; }
		public string Geolocation { get; set; }
		public string Type { get; set; }
		public string What3Words { get; set; }
		public string DispatchList { get; set; }
		public string ContactName { get; set; }
		public string ContactInfo { get; set; }
		public string ExternalId { get; set; }
		public string IncidentId { get; set; }
		public string ReferenceId { get; set; }
		public DateTimeOffset? DispatchOn { get; set; }

		/// <summary>
		/// Saves the call as pending: it waits in the dispatchers' queue and pages nobody until one of them sends it. With the
		/// system API key this is the only way a DispatchList of "0" (everyone) is honored.
		/// </summary>
		public bool? IsPending { get; set; }
		public string CallFormData { get; set; }
		public bool? CheckInTimersEnabled { get; set; }

		/// <summary>
		/// When set, scopes the call to a specific department.
		/// Required in hosted (multi-department) mode where the system-level
		/// API key can create calls for any department.
		/// </summary>
		public string DepartmentId { get; set; }
	}

	public sealed class SaveCallFileInput
	{
		public string CallId { get; set; }
		public string UserId { get; set; }
		public int Type { get; set; }
		public string Name { get; set; }
		public string Data { get; set; }
		public string Latitude { get; set; }
		public string Longitude { get; set; }
		public string Note { get; set; }

		/// <summary>
		/// When set, scopes the file upload to a specific department.
		/// Required in hosted (multi-department) mode where the system-level
		/// API key can upload files for any department.
		/// </summary>
		public string DepartmentId { get; set; }
	}

	public sealed class SaveOperationResult
	{
		public string Id { get; set; }
		public string Status { get; set; }
	}

	public sealed class GetCallResult
	{
		public CallResultData Data { get; set; }
		public string Status { get; set; }
	}

	public sealed class CallResultData
	{
		public string CallId { get; set; }
		public string Number { get; set; }
		public int Priority { get; set; }
		public string Name { get; set; }
		public string Nature { get; set; }
		public string Note { get; set; }
		public string Address { get; set; }
		public string Geolocation { get; set; }
		public DateTimeOffset LoggedOnUtc { get; set; }
		public string ContactName { get; set; }
		public string ContactInfo { get; set; }
		public string ReferenceId { get; set; }
		public string ExternalId { get; set; }
		public string IncidentId { get; set; }
		public string AudioFileId { get; set; }
		public string Type { get; set; }

		/// <summary>CallStates: Active = 0, Closed = 1 .. 7, Pending = 8.</summary>
		public int State { get; set; }

		/// <summary>
		/// The scheduled dispatch time (UTC) of a call created with DispatchOn; null for a call dispatched when it was created.
		/// A scheduled call is already "active" before it is sent.
		/// </summary>
		public DateTime? DispatchedOnUtc { get; set; }

		/// <summary>1 for the first dispatch; higher after the call has been escalated ("Second alarm").</summary>
		public int AlarmLevel { get; set; }
	}
}

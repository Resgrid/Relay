using System.Collections.Generic;

namespace Resgrid.Providers.ApiClient.V4.Models
{
	/// <summary>Input for POST /api/v4/Messages/SendMessage.</summary>
	public sealed class NewMessageInput
	{
		public string Title { get; set; }
		public string Body { get; set; }

		/// <summary>MessageTypes: Normal = 0, Callback = 1, Poll = 2.</summary>
		public int Type { get; set; }

		public List<MessageRecipientInput> Recipients { get; set; } = new List<MessageRecipientInput>();
	}

	public sealed class MessageRecipientInput
	{
		/// <summary>The user id, or "G:{groupId}" / "R:{roleId}".</summary>
		public string Id { get; set; }

		/// <summary>1 = person, 2 = group, 3 = role.</summary>
		public int Type { get; set; }

		/// <summary>
		/// Leave empty: Resgrid sends to the whole department when any recipient's Name is "Everyone", so echoing a group name
		/// here would broadcast department-wide if a group were ever called that.
		/// </summary>
		public string Name { get; set; }
	}

	public static class MessageRecipientTypes
	{
		public const int Person = 1;
		public const int Group = 2;
		public const int Role = 3;
	}
}

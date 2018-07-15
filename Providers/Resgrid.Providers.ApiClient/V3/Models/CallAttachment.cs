using System;

namespace Resgrid.Providers.ApiClient.V3.Models
{
    public class CallAttachment
    {
	    public int CallAttachmentId { get; set; }
	    public int CallId { get; set; }
	    public int CallAttachmentType { get; set; }
	    public string FileName { get; set; }
	    public byte[] Data { get; set; }
	    public string UserId { get; set; }
	    public DateTime? Timestamp { get; set; }
	    public string Name { get; set; }
	    public int? Size { get; set; }
	    public decimal? Latitude { get; set; }
	    public decimal? Longitude { get; set; }
	}
}

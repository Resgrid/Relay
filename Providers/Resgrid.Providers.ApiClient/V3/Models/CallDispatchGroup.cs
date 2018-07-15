using System;

namespace Resgrid.Providers.ApiClient.V3.Models
{
    public class CallDispatchGroup
    {
	    public int CallDispatchGroupId { get; set; }
	    public int CallId { get; set; }
	    public int DepartmentGroupId { get; set; }
	    public int DispatchCount { get; set; }
	    public DateTime? LastDispatchedOn { get; set; }
	}
}

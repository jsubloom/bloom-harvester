using System;

namespace BloomHarvester
{
	public enum HarvestMode
	{
		ForceAll,	// Same as "All" but will process books even if they are reported as In Progress.
		All,	// Processes all books normally, except for books that are already In Progress by something else
		Default,
		RetryFailuresOnly,
		HarvestNeeded,	// includes New or Updated or Requested (or Unknown)
	}
}

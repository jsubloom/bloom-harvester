namespace BloomHarvester.Parse.Model
{
	public enum HarvestState
	{
		Unknown,
		New, // set by parse code when the user uploads a book
		Updated, // set by parse code when the user re-uploads a book
		InProgress,
		Done,
		Failed,
		FailedIndefinitely, // set manually for books that shouldn't be retried until the book is updated
		Aborted,
		Requested	// set manually for books that need to be retried asap
	}
}

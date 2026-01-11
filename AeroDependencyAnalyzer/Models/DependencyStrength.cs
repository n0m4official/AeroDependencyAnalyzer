namespace AeroDependencyAnalyzer.Models
{
	public enum DependencyStrength
	{
		Informational,	// for display only
		Minor,			// might degrade
		Major,			// degradation likely
		Critical		// can force failure
	}
}

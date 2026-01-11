using System;

namespace AeroDependencyAnalyzer.Models
{
	public sealed class DependencyEdge
	{
		// A -> B means "A depends on B"
		public Guid FromId { get; init; } // A (dependent)
		public Guid ToId { get; init; }   // B (dependency)

		public DependencyStrength Strength { get; set; } = DependencyStrength.Major;
	}
}

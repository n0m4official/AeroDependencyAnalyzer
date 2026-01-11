using System;
using System.Collections.Generic;

namespace AeroDependencyAnalyzer.Models
{
	public sealed class SystemNode
	{
		public Guid Id { get; } = Guid.NewGuid();

		public string Name { get; set; } = "New System";

		public SystemType Type { get; set; } = SystemType.Other;

		// Redundancy grouping: e.g. "ELEC_BUS_A", "ELEC_BUS_B", "HYD_SYS_1", "HYD_SYS_2"
		// If you leave it empty, it behaves as non-redundant.
		public string RedundancyGroup { get; set; } = "";

		public SystemStatus Status { get; set; } = SystemStatus.Nominal;

		public FailureMode FailureMode { get; set; } = FailureMode.None;

		// Diagram placement
		public double X { get; set; } = 100;
		public double Y { get; set; } = 100;

		// A depends on B => edge A -> B
		public HashSet<Guid> DependsOn { get; } = new();
		public HashSet<Guid> Dependents { get; } = new();
	}
}

using AeroDependencyAnalyzer.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace AeroDependencyAnalyzer
{
	public partial class MainWindow : Window
	{
		// In-memory model
		private readonly Dictionary<Guid, SystemNode> _nodes = new();
		private readonly List<DependencyEdge> _edges = new();

		// UI mapping: node id -> element
		private readonly Dictionary<Guid, Border> _nodeElements = new();

		// UI mapping: edge -> line
		private readonly Dictionary<DependencyEdge, Line> _edgeLines = new();

		private readonly Dictionary<DependencyEdge, Path> _edgeArrows = new();

		// Selection & modes
		private Guid? _selectedNodeId = null;

		private bool _isAddDependencyMode = false;
		private Guid? _pendingDependencySourceId = null;

		// Drag state
		private bool _isDragging = false;
		private Point _dragStartMouse;
		private Point _dragStartNodePos;
		private Guid? _dragNodeId = null;

		// Analysis overlays
		private readonly Dictionary<Guid, SystemStatus> _baselineStatus = new();

		private readonly Dictionary<(Guid From, Guid To), DependencyEdge> _edgeLookup = new();

		public MainWindow()
		{
			InitializeComponent();
			SeedExample();
			RedrawAll();
		}

		private void SeedExample()
		{
			// A small sample graph so it doesn't start empty
			var electrical = new SystemNode { Name = "Electrical", X = 120, Y = 140 };
			var avionics = new SystemNode { Name = "Avionics", X = 360, Y = 100 };
			var autopilot = new SystemNode { Name = "Autopilot", X = 620, Y = 120 };
			var hydraulics = new SystemNode { Name = "Hydraulics", X = 360, Y = 260 };
			var flightControls = new SystemNode { Name = "Flight Controls", X = 620, Y = 260 };

			AddNode(electrical);
			AddNode(avionics);
			AddNode(autopilot);
			AddNode(hydraulics);
			AddNode(flightControls);

			// Avionics depends on Electrical
			AddEdge(avionics.Id, electrical.Id);

			// Autopilot depends on Avionics
			AddEdge(autopilot.Id, avionics.Id);

			// Flight Controls depends on Hydraulics and Electrical (example)
			AddEdge(flightControls.Id, hydraulics.Id);
			AddEdge(flightControls.Id, electrical.Id);
		}

		// -----------------------------
		//  Node/Edge model operations
		// -----------------------------
		private void AddNode(SystemNode node)
		{
			_nodes[node.Id] = node;
		}

		private void AddEdge(Guid fromId, Guid toId)
		{
			if (fromId == toId) return;

			// prevent duplicates
			if (_edgeLookup.ContainsKey((fromId, toId))) return;

			var edge = new DependencyEdge { FromId = fromId, ToId = toId, Strength = DependencyStrength.Major };
			_edges.Add(edge);
			_edgeLookup[(fromId, toId)] = edge;

			// Update adjacency
			_nodes[fromId].DependsOn.Add(toId);
			_nodes[toId].Dependents.Add(fromId);
		}

		private void RemoveAllAnalysis()
		{
			LstExplanations.Items.Clear();
			_baselineStatus.Clear();
			RedrawAll();
		}

		// -----------------------------
		//  UI: Buttons
		// -----------------------------
		private void BtnAddSystem_Click(object sender, RoutedEventArgs e)
		{
			var node = new SystemNode
			{
				Name = $"System {_nodes.Count + 1}",
				X = 140 + (_nodes.Count * 20) % 500,
				Y = 140 + (_nodes.Count * 25) % 350
			};

			AddNode(node);
			RedrawAll();
			SelectNode(node.Id);
		}

		private void BtnAddDependency_Click(object sender, RoutedEventArgs e)
		{
			_isAddDependencyMode = true;
			_pendingDependencySourceId = null;
			TxtModeHint.Text = "Mode: Add Dependency — click SOURCE node (dependent), then TARGET node (dependency).";
		}

		private void BtnRunAnalysis_Click(object sender, RoutedEventArgs e)
		{
			if (_selectedNodeId is null)
			{
				MessageBox.Show("Select a node first. Then set it to Failed (or Degraded) and run analysis.", "No selection");
				return;
			}

			// Store baseline so we can clear later
			_baselineStatus.Clear();
			foreach (var n in _nodes.Values)
				_baselineStatus[n.Id] = n.Status;

			RunFailurePropagationAnalysis();
			RedrawAll();
		}

		private void BtnClearAnalysis_Click(object sender, RoutedEventArgs e)
		{
			if (_baselineStatus.Count > 0)
			{
				foreach (var kv in _baselineStatus)
					_nodes[kv.Key].Status = kv.Value;

				_baselineStatus.Clear();
			}

			LstExplanations.Items.Clear();
			RedrawAll();
		}

		private void SetNominal_Click(object sender, RoutedEventArgs e)
		{
			if (_selectedNodeId is null) return;
			_nodes[_selectedNodeId.Value].Status = SystemStatus.Nominal;
			RedrawAll();
		}

		private void SetDegraded_Click(object sender, RoutedEventArgs e)
		{
			if (_selectedNodeId is null) return;
			_nodes[_selectedNodeId.Value].Status = SystemStatus.Degraded;
			RedrawAll();
		}

		private void SetFailed_Click(object sender, RoutedEventArgs e)
		{
			if (_selectedNodeId is null) return;
			_nodes[_selectedNodeId.Value].Status = SystemStatus.Failed;
			RedrawAll();
		}

		private void TxtName_TextChanged(object sender, TextChangedEventArgs e)
		{
			if (_selectedNodeId is null) return;
			_nodes[_selectedNodeId.Value].Name = TxtName.Text;
			RedrawAll();
		}

		// -----------------------------
		//  Analysis logic
		// -----------------------------
		private void RunFailurePropagationAnalysis()
		{
			LstExplanations.Items.Clear();

			var failedStart = _nodes.Values.Where(n => n.Status == SystemStatus.Failed)
				.Select(n => n.Id).ToList();

			if (failedStart.Count == 0)
			{
				MessageBox.Show("Mark at least one system as Failed before running analysis.", "No failed systems");
				return;
			}

			// Precompute redundancy group health:
			// group -> (total count, failed count)
			var groupStats = _nodes.Values
				.Where(n => !string.IsNullOrWhiteSpace(n.RedundancyGroup))
				.GroupBy(n => n.RedundancyGroup.Trim())
				.ToDictionary(
					g => g.Key,
					g => new
					{
						Total = g.Count(),
						Failed = g.Count(n => n.Status == SystemStatus.Failed)
					}
				);

			bool IsGroupFullyFailed(SystemNode n)
			{
				var key = (n.RedundancyGroup ?? "").Trim();
				if (string.IsNullOrWhiteSpace(key)) return true; // treat non-grouped nodes as "fully failed if they fail"
				if (!groupStats.TryGetValue(key, out var st)) return true;
				return st.Failed >= st.Total;
			}

			var q = new Queue<Guid>(failedStart);
			var inQueue = new HashSet<Guid>(failedStart);

			var reason = new Dictionary<Guid, string>();
			foreach (var s in failedStart)
				reason[s] = $"{_nodes[s].Name} is FAILED (source).";

			while (q.Count > 0)
			{
				var currentId = q.Dequeue();
				inQueue.Remove(currentId);

				var current = _nodes[currentId];

				foreach (var dependentId in current.Dependents)
				{
					var dependent = _nodes[dependentId];

					// Determine strength of relationship dependent -> current
					if (!_edgeLookup.TryGetValue((dependentId, currentId), out var edge))
						continue;

					// If dependency isn't failed, it doesn't contribute to cascade.
					if (current.Status != SystemStatus.Failed)
						continue;

					var before = dependent.Status;

					// Redundancy-aware: if current is in a redundancy group and the group isn't fully failed,
					// we cap the effect at Degraded.
					bool groupFullyFailed = IsGroupFullyFailed(current);

					SystemStatus proposed = before;

					switch (edge.Strength)
					{
						case DependencyStrength.Critical:
							proposed = groupFullyFailed ? SystemStatus.Failed : SystemStatus.Degraded;
							break;

						case DependencyStrength.Major:
							if (before == SystemStatus.Nominal)
								proposed = SystemStatus.Degraded;
							break;

						case DependencyStrength.Minor:
							// Minor: only nudge Nominal -> Degraded
							if (before == SystemStatus.Nominal)
								proposed = SystemStatus.Degraded;
							break;

						case DependencyStrength.Informational:
							break;
					}

					// Apply if worse
					if (proposed > before)
					{
						dependent.Status = proposed;

						// Add explanation (keep it short but specific)
						var strengthTxt = edge.Strength.ToString().ToUpperInvariant();
						if (proposed == SystemStatus.Failed)
							reason[dependentId] = $"{dependent.Name} FAILED due to {strengthTxt} dependency on {current.Name}.";
						else
							reason[dependentId] = $"{dependent.Name} degraded due to {strengthTxt} dependency on {current.Name}.";

						// Continue propagation
						if (!inQueue.Contains(dependentId))
						{
							q.Enqueue(dependentId);
							inQueue.Add(dependentId);
						}
					}
				}
			}

			foreach (var line in reason.Values.OrderBy(s => s))
				LstExplanations.Items.Add(line);
		}

		// -----------------------------
		//  Rendering
		// -----------------------------
		private void RedrawAll()
		{
			DiagramCanvas.Children.Clear();
			_nodeElements.Clear();
			_edgeLines.Clear();
			_edgeArrows.Clear();

			// Draw edges first (behind nodes)
			foreach (var edge in _edges)
			{
				var line = new Line
				{
					StrokeThickness = 2,
					Stroke = new SolidColorBrush(Color.FromRgb(110, 110, 110)),
					SnapsToDevicePixels = true
				};

				var arrow = new Path
				{
					Fill = new SolidColorBrush(Color.FromRgb(110, 110, 110)),
					Data = Geometry.Empty
				};

				_edgeLines[edge] = line;
				_edgeArrows[edge] = arrow;

				DiagramCanvas.Children.Add(line);
				DiagramCanvas.Children.Add(arrow);
			}

			// Draw nodes
			foreach (var node in _nodes.Values)
			{
				var border = CreateNodeElement(node);
				_nodeElements[node.Id] = border;

				DiagramCanvas.Children.Add(border);
				Canvas.SetLeft(border, node.X);
				Canvas.SetTop(border, node.Y);
			}

			// Update edge positions
			UpdateAllEdgePositions();

			// Update name box for selection
			if (_selectedNodeId is not null && _nodes.TryGetValue(_selectedNodeId.Value, out var selected))
			{
				TxtName.TextChanged -= TxtName_TextChanged;
				TxtName.Text = selected.Name;
				TxtName.TextChanged += TxtName_TextChanged;
			}
		}

		private void UpdateArrow(DependencyEdge edge, double x1, double y1, double x2, double y2)
		{
			if (!_edgeArrows.TryGetValue(edge, out var arrow)) return;

			// direction vector
			var dx = x2 - x1;
			var dy = y2 - y1;
			var len = Math.Sqrt(dx * dx + dy * dy);
			if (len < 0.001) { arrow.Data = Geometry.Empty; return; }

			dx /= len; dy /= len;

			// pull the tip back so it doesn't sit inside the node
			const double tipBack = 45;
			x2 -= dx * tipBack;
			y2 -= dy * tipBack;

			// arrow size
			const double size = 10;

			// point near the end of line
			var px = x2 - dx * 12;
			var py = y2 - dy * 12;

			// perpendicular
			var perpX = -dy;
			var perpY = dx;

			var p1 = new Point(x2, y2);
			var p2 = new Point(px + perpX * size / 2, py + perpY * size / 2);
			var p3 = new Point(px - perpX * size / 2, py - perpY * size / 2);

			var geom = new PathGeometry();
			var fig = new PathFigure { StartPoint = p1, IsClosed = true };
			fig.Segments.Add(new LineSegment(p2, true));
			fig.Segments.Add(new LineSegment(p3, true));
			geom.Figures.Add(fig);

			arrow.Data = geom;
		}


		private Border CreateNodeElement(SystemNode node)
		{
			var border = new Border
			{
				Width = 170,
				Height = 62,
				CornerRadius = new CornerRadius(10),
				BorderThickness = new Thickness(2),
				Padding = new Thickness(10),
				Background = GetStatusFill(node.Status),
				BorderBrush = GetStatusBorder(node.Status),
				Tag = node.Id
			};

			var text = new TextBlock
			{
				Text = node.Name,
				Foreground = Brushes.White,
				FontWeight = FontWeights.SemiBold,
				TextWrapping = TextWrapping.Wrap
			};

			border.Child = text;

			// Selection + modes
			border.MouseLeftButtonDown += Node_MouseLeftButtonDown;
			border.MouseMove += Node_MouseMove;
			border.MouseLeftButtonUp += Node_MouseLeftButtonUp;

			// Right click quick fail toggle (nice demo trick)
			border.MouseRightButtonDown += (s, e) =>
			{
				SelectNode(node.Id);
				var n = _nodes[node.Id];
				n.Status = n.Status == SystemStatus.Failed ? SystemStatus.Nominal : SystemStatus.Failed;
				RedrawAll();
			};

			// Highlight selection
			if (_selectedNodeId == node.Id)
				border.BorderBrush = Brushes.White;

			return border;
		}

		private Brush GetStatusFill(SystemStatus status) =>
			status switch
			{
				SystemStatus.Nominal => new SolidColorBrush(Color.FromRgb(34, 46, 58)),
				SystemStatus.Degraded => new SolidColorBrush(Color.FromRgb(86, 76, 34)),
				SystemStatus.Failed => new SolidColorBrush(Color.FromRgb(86, 34, 34)),
				_ => new SolidColorBrush(Color.FromRgb(34, 46, 58))
			};

		private Brush GetStatusBorder(SystemStatus status) =>
			status switch
			{
				SystemStatus.Nominal => new SolidColorBrush(Color.FromRgb(85, 140, 210)),
				SystemStatus.Degraded => new SolidColorBrush(Color.FromRgb(220, 190, 80)),
				SystemStatus.Failed => new SolidColorBrush(Color.FromRgb(230, 90, 90)),
				_ => new SolidColorBrush(Color.FromRgb(85, 140, 210))
			};

		private void UpdateAllEdgePositions()
		{
			foreach (var edge in _edges)
			{
				if (!_nodes.TryGetValue(edge.FromId, out var from)) continue;
				if (!_nodes.TryGetValue(edge.ToId, out var to)) continue;
				if (!_edgeLines.TryGetValue(edge, out var line)) continue;

				var (fx, fy) = NodeCenter(from);
				var (tx, ty) = NodeCenter(to);

				line.X1 = fx;
				line.Y1 = fy;
				line.X2 = tx;
				line.Y2 = ty;

				UpdateArrow(edge, fx, fy, tx, ty);
			}
		}


		private (double x, double y) NodeCenter(SystemNode node)
		{
			// Match Border width/height above
			const double w = 170;
			const double h = 62;
			return (node.X + w / 2, node.Y + h / 2);
		}

		// -----------------------------
		//  Interaction: selection, drag, dependency mode
		// -----------------------------
		private void SelectNode(Guid id)
		{
			_selectedNodeId = id;

			TxtName.TextChanged -= TxtName_TextChanged;
			TxtName.Text = _nodes[id].Name;
			TxtName.TextChanged += TxtName_TextChanged;

			// If we're in dependency mode, interpret clicks differently
			if (_isAddDependencyMode)
			{
				HandleDependencyClick(id);
			}
		}

		private void DeleteSelectedNode()
		{
			if (_selectedNodeId is null) return;
			var id = _selectedNodeId.Value;

			// Remove edges linked to node
			var toRemove = _edges.Where(e => e.FromId == id || e.ToId == id).ToList();
			foreach (var e in toRemove)
			{
				_edges.Remove(e);
				_edgeLookup.Remove((e.FromId, e.ToId));
			}

			// Remove from adjacency sets
			foreach (var n in _nodes.Values)
			{
				n.DependsOn.Remove(id);
				n.Dependents.Remove(id);
			}

			_nodes.Remove(id);
			_selectedNodeId = null;
			TxtName.Text = "";
			RedrawAll();
		}


		private void HandleDependencyClick(Guid clickedId)
		{
			if (_pendingDependencySourceId is null)
			{
				_pendingDependencySourceId = clickedId;
				TxtModeHint.Text = $"Mode: Add Dependency — SOURCE selected: {_nodes[clickedId].Name}. Now click TARGET (dependency).";
				return;
			}

			var source = _pendingDependencySourceId.Value;
			var target = clickedId;

			if (source == target)
			{
				TxtModeHint.Text = "Mode: Add Dependency — target cannot be the same as source. Click a different node.";
				return;
			}

			AddEdge(source, target);

			_isAddDependencyMode = false;
			_pendingDependencySourceId = null;
			TxtModeHint.Text = "Mode: Normal";

			RedrawAll();
		}

		private void Node_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
		{
			if (sender is not Border b || b.Tag is not Guid id) return;

			// Always select on click
			SelectNode(id);

			// In dependency mode, don't start drag (keeps it clean)
			if (_isAddDependencyMode) return;

			// Begin drag
			_isDragging = true;
			_dragNodeId = id;
			_dragStartMouse = e.GetPosition(DiagramCanvas);
			_dragStartNodePos = new Point(_nodes[id].X, _nodes[id].Y);

			b.CaptureMouse();
		}

		private void Node_MouseMove(object sender, MouseEventArgs e)
		{
			if (!_isDragging || _dragNodeId is null) return;
			if (sender is not Border b) return;

			var curMouse = e.GetPosition(DiagramCanvas);
			var dx = curMouse.X - _dragStartMouse.X;
			var dy = curMouse.Y - _dragStartMouse.Y;

			var id = _dragNodeId.Value;
			_nodes[id].X = _dragStartNodePos.X + dx;
			_nodes[id].Y = _dragStartNodePos.Y + dy;

			Canvas.SetLeft(b, _nodes[id].X);
			Canvas.SetTop(b, _nodes[id].Y);

			UpdateAllEdgePositions();
		}

		private void Node_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
		{
			if (sender is not Border b) return;

			_isDragging = false;
			_dragNodeId = null;
			b.ReleaseMouseCapture();

			UpdateAllEdgePositions();
		}
	}
}

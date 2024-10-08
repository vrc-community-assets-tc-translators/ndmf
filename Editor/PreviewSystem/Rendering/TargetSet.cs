﻿using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using UnityEngine;
using UnityEngine.Profiling;

namespace nadena.dev.ndmf.preview
{
    internal class TargetSet
    {
        private static readonly Comparer<RenderGroup> GroupComparer = Comparer<RenderGroup>.Create(
            (a, b) => a.Renderers.First().GetInstanceID().CompareTo(b.Renderers.First().GetInstanceID())
        );

        private readonly ImmutableList<IRenderFilter> _filters;
        private readonly ComputeContext _targetSetContext = new ComputeContext("Target Set");
        private ImmutableList<Stage> _stages;

        
        public struct Stage
        {
            public IRenderFilter Filter;
            public ImmutableList<RenderGroup> Groups; 
        }
        
        public TargetSet(ImmutableList<IRenderFilter> filters)
        {
            _filters = filters;
            
            Profiler.BeginSample("TargetSet.ctor");
            try
            {
                var builder = ImmutableList.CreateBuilder<Stage>();
                foreach (var filter in _filters)
                {
                    if (!filter.IsEnabled(_targetSetContext)) continue;

                    Profiler.BeginSample("TargetSet.GetTargetGroups[" + filter + "]");
                    var groups = filter.GetTargetGroups(_targetSetContext);
                    if (groups.IsEmpty) continue;
                    
                    builder.Add(new Stage
                    {
                        Filter = filter,
                        Groups = groups.Sort(GroupComparer)
                    });
                }
                
                _stages = builder.ToImmutable();
            }
            finally
            {
                Profiler.EndSample();
            }
        }
        
        public TargetSet Refresh(ImmutableList<IRenderFilter> filters)
        {
            if (!_targetSetContext.IsInvalidated && _filters.SequenceEqual(filters))
            {
                return this;
            }
            
            return new TargetSet(filters);
        }

        private static bool RendererIsShown(ComputeContext context, Renderer renderer)
        {
            if (renderer == null) return false;
            if (!context.ActiveInHierarchy(renderer.gameObject)) return false;

            return context.Observe(renderer, r => r.enabled && !r.forceRenderingOff);
        }
        
        public ImmutableList<Stage> ResolveActiveStages(ComputeContext context)
        {
            Profiler.BeginSample("TargetSet.ResolveActiveStages");
            _targetSetContext.Invalidates(context);
            VisibilityMonitor.OnVisibilityChange.Register(_ => true, context);

            HashSet<Renderer> maybeActiveRenderers = new HashSet<Renderer>(new ObjectIdentityComparer<Renderer>());
            
            // Register all visible (or potentially forced) renderers first
            foreach (var stage in _stages)
            {
                foreach (var group in stage.Groups)
                {
                    foreach (var renderer in group.Renderers)
                    {
                        if (RendererIsShown(context, renderer) || stage.Filter.CanEnableRenderers)
                        {
                            maybeActiveRenderers.Add(renderer);
                        }
                    }
                }
            }
            
            // If a maybe-active renderer is in the same target group as an inactive renderer, and the filter for that
            // group is marked strict, we need to force all of its neighbors in the group as well. This then proceeds up
            // to earlier stages in the pipeline.
            for (int i = _stages.Count - 1; i >= 0; i--)
            {
                var stage = _stages[i];
                if (!stage.Filter.StrictRenderGroup) continue;
                
                foreach (var group in _stages[i].Groups)
                {
                    bool anyActive = group.Renderers.Any(maybeActiveRenderers.Contains);

                    if (anyActive)
                    {
                        foreach (var renderer in group.Renderers)
                        {
                            maybeActiveRenderers.Add(renderer);
                        }
                    }
                }
            }
            
            // Now rebuild the stages considering the maybe-active set.
            var builder = ImmutableList.CreateBuilder<Stage>();
            
            foreach (var stage in _stages)
            {
                var activeGroups = ImmutableList.CreateBuilder<RenderGroup>();
                foreach (var group in stage.Groups)
                {
                    RenderGroup filtered = group.Filter(maybeActiveRenderers);
                    if (!filtered.IsEmpty)
                    {
                        activeGroups.Add(filtered);
                    }
                }

                if (activeGroups.Count > 0)
                {
                    builder.Add(new Stage
                    {
                        Filter = stage.Filter,
                        Groups = activeGroups.ToImmutable()
                    });
                }
            }
            
            Profiler.EndSample();
            
            return builder.ToImmutable();
        }
    }
}
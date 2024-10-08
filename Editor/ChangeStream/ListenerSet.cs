﻿#region

using System;
using System.Collections.Generic;
using nadena.dev.ndmf.preview;
using UnityEditor;

#endregion

namespace nadena.dev.ndmf.cs
{
    internal class Listener<T> : IDisposable
    {
        internal Listener<T> _next, _prev;

        private readonly ListenerSet<T> _owner;
        private readonly ListenerSet<T>.Filter _filter;
        private readonly WeakReference<object> _target;
        private readonly Action<object> _receiver;

        internal Listener(
            ListenerSet<T> owner,
            ListenerSet<T>.Filter filter,
            ComputeContext ctx
        ) : this(owner, filter, ctx, InvalidateContext)
        {
        }
        
        private static void InvalidateContext(object ctx)
        {
            ((ComputeContext) ctx).Invalidate();
        }
        
        internal Listener(
            ListenerSet<T> owner,
            ListenerSet<T>.Filter filter,
            object target,
            Action<object> receiver
        )
        {
            _owner = owner;
            _next = _prev = this;
            _filter = filter;
            _target = new WeakReference<object>(target);
            _receiver = receiver;
        }

        public override string ToString()
        {
            if (_target.TryGetTarget(out var target))
                return $"Listener for {target}";
            return "Listener (GC'd)";
        }

        public void Dispose()
        {
            lock (_owner)
            {
                if (_next != null)
                {
                    _next._prev = _prev;
                    _prev._next = _next;
                }

                _next = _prev = null;
                _target.SetTarget(null);
            }
        }

        internal void MaybePrune()
        {
            if (!_target.TryGetTarget(out _))
            {
#if NDMF_DEBUG
                System.Diagnostics.Debug.WriteLine($"{this} is invalid, disposing");
#endif
                Dispose();
            }
        }

        // Invoked under lock(_owner)
        internal void MaybeFire(T info)
        {
            if (!_target.TryGetTarget(out var target))
            {
#if NDMF_DEBUG
                System.Diagnostics.Debug.WriteLine($"{this} is invalid, disposing");
#endif
                Dispose();
            }
            else if (_filter(info))
            {
#if NDMF_DEBUG
                System.Diagnostics.Debug.WriteLine($"{this} is firing");
#endif

                _receiver(target);
                // We need to wait two frames before repainting: One to process task callbacks, then one to actually
                // repaint (and update previews).
                EditorApplication.delayCall += Delay2Repaint;
                Dispose();
            }
        }

        private void Delay2Repaint()
        {
            EditorApplication.delayCall += SceneView.RepaintAll;
        }

        public void ForceFire()
        {
            if (_target.TryGetTarget(out var ctx)) _receiver(ctx);
            _target.SetTarget(null);
        }
    }

    internal class ListenerSet<T>
    {
        public delegate bool Filter(T info);

        private Listener<T> _head;

        public ListenerSet()
        {
            _head = new Listener<T>(this, _ => false, null);
            _head._next = _head._prev = _head;
        }

        public bool HasListeners()
        {
            return _head._next != _head;
        }

        public IDisposable Register(Filter filter, ComputeContext ctx)
        {
            var listener = new Listener<T>(this, filter, ctx);

            lock (this)
            {
                listener._next = _head._next;
                listener._prev = _head;
                _head._next._prev = listener;
                _head._next = listener;
            }

            return listener;
        }
        
        public IDisposable Register(Filter filter, object target, Action<object> receiver)
        {
            var listener = new Listener<T>(this, filter, target, receiver);

            lock (this)
            {
                listener._next = _head._next;
                listener._prev = _head;
                _head._next._prev = listener;
                _head._next = listener;
            }

            return listener;
        }

        public IDisposable Register(ComputeContext ctx)
        {
            return Register(PassAll, ctx);
        }
        
        public IDisposable Register(object target, Action<object> receiver)
        {
            return Register(PassAll, target, receiver);
        }
        
        private static bool PassAll(T _) => true;

        public void Fire(T info)
        {
            for (var listener = _head._next; listener != _head;)
            {
                var next = listener._next;
                listener.MaybeFire(info);
                listener = next;
            }
        }

        public void Prune()
        {
            for (var listener = _head._next; listener != _head;)
            {
                var next = listener._next;
                listener.MaybePrune();
                listener = next;
            }
        }
        
        internal IEnumerable<string> GetListeners()
        {
            var ptr = _head._next;
            while (ptr != _head)
            {
                yield return ptr.ToString();
                ptr = ptr._next;
            }
        }

        public void FireAll()
        {
            for (var listener = _head._next; listener != _head;)
            {
                var next = listener._next;
                listener.ForceFire();
                listener = next;
            }

            _head._next = _head._prev = _head;
        }
    }
}
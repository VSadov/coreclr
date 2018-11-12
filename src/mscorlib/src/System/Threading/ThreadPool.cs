// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

/*=============================================================================
**
**
**
** Purpose: Class for creating and managing a threadpool
**
**
=============================================================================*/

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Tracing;
using System.Runtime.CompilerServices;
using System.Runtime.ConstrainedExecution;
using System.Runtime.InteropServices;
using System.Security;
using Internal.Runtime.Augments;
using Internal.Runtime.CompilerServices;
using Microsoft.Win32;

//TODO: VS remove
//[assembly: System.Diagnostics.Debuggable(true, true)]

namespace System.Threading
{
    internal static class ThreadPoolGlobals
    {
        //Per-appDomain quantum (in ms) for which the thread keeps processing
        //requests in the current domain.
        public const uint TP_QUANTUM = 30U;

        public static readonly int processorCount = Environment.ProcessorCount;

        public static volatile bool vmTpInitialized;
        public static bool enableWorkerTracking;

        public static readonly ThreadPoolWorkQueue workQueue = new ThreadPoolWorkQueue();
    }

    [StructLayout(LayoutKind.Sequential)] // enforce layout so that padding reduces false sharing
    internal sealed class ThreadPoolWorkQueue
    {
        internal class WorkQueueBase
        {
            // This implementation provides an unbounded, multi-producer multi-consumer queue
            // that supports the standard Enqueue/Dequeue operations.
            // It is composed of a linked list of bounded ring buffers, each of which has a head
            // and a tail index, isolated from each other to minimize false sharing.  As long as
            // the number of elements in the queue remains less than the size of the current
            // buffer (Segment), no additional allocations are required for enqueued items.  When
            // the number of items exceeds the size of the current segment, the current segment is
            // "frozen" to prevent further enqueues, and a new segment is linked from it and set
            // as the new tail segment for subsequent enqueues.  As old segments are consumed by
            // dequeues, the head reference is updated to point to the segment that dequeuers should
            // try next.
            // The queue also supports Pop operation from the enqueuing end or by selectively removing
            // particular items. 
            // Items can be Popped only from the tail segment. 
            // Older segments are supposed to be consumed by dequeues and retired.

            /// <summary>
            /// Initial length of the segments used in the queue. 
            /// </summary>
            internal static int InitialSegmentLength = 32;

            /// <summary>
            /// Maximum length of the segments used in the queue.  This is a somewhat arbitrary limit:
            /// larger means that as long as we don't exceed the size, we avoid allocating more segments,
            /// but if we do exceed it, then the segment becomes garbage.
            /// </summary>
            internal const int MaxSegmentLength = 1024 * 1024;

            /// <summary>
            /// Lock used to protect cross-segment operations, including any updates to <see cref="_enqSegment"/> or <see cref="_deqSegment"/>
            /// and any operations that need to get a consistent view of them.
            /// </summary>
            internal object _crossSegmentLock;

            /// <summary>Padded head and tail indices, to avoid false sharing between producers and consumers.</summary>
            [DebuggerDisplay("Head = {Head}, Tail = {Tail}, Pop = {Pop}")]
            [StructLayout(LayoutKind.Explicit, Size = 3 * Internal.PaddingHelpers.CACHE_LINE_SIZE)] // padding before/between/after fields
            internal struct PaddedHeadAndTail
            {
                [FieldOffset(1 * Internal.PaddingHelpers.CACHE_LINE_SIZE)] public int Dequeue;
                [FieldOffset(2 * Internal.PaddingHelpers.CACHE_LINE_SIZE)] public int Enqueue;
            }

            internal class QueueSegmentBase
            {
                // Segment design is inspired by the algorithm outlined at:
                // http://www.1024cores.net/home/lock-free-algorithms/queues/bounded-mpmc-queue

                /// <summary>The array of items in this queue.  Each slot contains the item in that slot and its "sequence number".</summary>
                internal readonly Slot[] _slots;

                /// <summary>Mask for quickly accessing a position within the queue's array.</summary>
                internal readonly int _slotsMask;

                /// <summary>The head and tail positions, with padding to help avoid false sharing contention.</summary>
                /// <remarks>Dequeuing happens from the head, enqueuing happens at the tail.</remarks>
                internal PaddedHeadAndTail _queueEnds; // mutable struct: do not make this readonly

                /// <summary>Indicates whether the segment has been marked such that no additional items may be enqueued.</summary>
                internal bool _frozenForEnqueues;

                internal const int Empty = 0;
                internal const int Full = 1;

                /// <summary>Creates the segment.</summary>
                /// <param name="boundedLength">
                /// The maximum number of elements the segment can contain.  Must be a power of 2.
                /// </param>
                internal QueueSegmentBase(int boundedLength)
                {
                    // Validate the length
                    Debug.Assert(boundedLength >= 2, $"Must be >= 2, got {boundedLength}");
                    Debug.Assert((boundedLength & (boundedLength - 1)) == 0, $"Must be a power of 2, got {boundedLength}");

                    // Initialize the slots and the mask.  The mask is used as a way of quickly doing "% _slots.Length",
                    // instead letting us do "& _slotsMask".
                    var slots = new Slot[boundedLength];
                    _slotsMask = boundedLength - 1;

                    // Initialize the sequence number for each slot.  The sequence number provides a ticket that
                    // allows dequeuers to know whether they can dequeue and enqueuers to know whether they can
                    // enqueue.  An enqueuer at position N can enqueue when the sequence number is N, and a dequeuer
                    // for position N can dequeue when the sequence number is N + 1.  When an enqueuer is done writing
                    // at position N, it sets the sequence number to N + 1 so that a dequeuer will be able to dequeue,
                    // and when a dequeuer is done dequeueing at position N, it sets the sequence number to N + _slots.Length,
                    // so that when an enqueuer loops around the slots, it'll find that the sequence number at
                    // position N is N.  This also means that when an enqueuer finds that at position N the sequence
                    // number is < N, there is still a value in that slot, i.e. the segment is full, and when a
                    // dequeuer finds that the value in a slot is < N + 1, there is nothing currently available to
                    // dequeue. (It is possible for multiple enqueuers to enqueue concurrently, writing into
                    // subsequent slots, and to have the first enqueuer take longer, so that the slots for 1, 2, 3, etc.
                    // may have values, but the 0th slot may still be being filled... in that case, TryDequeue will
                    // return false.)
                    for (int i = 0; i < slots.Length; i++)
                    {
                        slots[i].SequenceNumber = i;
                    }

                    this._slots = slots;
                }

                /// <summary>Represents a slot in the queue.</summary>
                [DebuggerDisplay("Item = {Item}, SequenceNumber = {SequenceNumber}")]
                [StructLayout(LayoutKind.Auto)]
                internal struct Slot
                {
                    /// <summary>The item.</summary>
                    internal IThreadPoolWorkItem Item;
                    /// <summary>The sequence number for this slot, used to synchronize between enqueuers and dequeuers.</summary>
                    internal int SequenceNumber;
                }

                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                internal static ref Slot GetSlot(Slot[] slots, int slotsMask, int position)
                {
                    return ref Unsafe.Add(ref Unsafe.As<byte, Slot>(ref slots.GetRawSzArrayData()), position & slotsMask);
                }

                internal bool CanSteal
                {
                    [MethodImpl(MethodImplOptions.AggressiveInlining)]
                    get
                    {
                        // Read Deq and then Enq. If not the same, there could be work for a dequeuer. 
                        // NB: order of reads is unimportant here. 
                        //     threads will do interlocked op on dispatch so will observe new items. that is enough for correctness.
                        //     here if Deq == Enq, we have no work at this point in time.
                        // NB: frozen segments have artificially increased size and will appear as having work even when there are no items.
                        return _queueEnds.Dequeue != _queueEnds.Enqueue;
                    }
                }

                /// <summary>Gets the "freeze offset" for this segment.</summary>
                internal int FreezeOffset => _slots.Length * 2;
            }
        }

        internal class GlobalQueue: WorkQueueBase
        {
            /// <summary>The current enqueue segment.</summary>
            private GlobalQueueSegment _enqSegment;
            /// <summary>The current dequeue segment.</summary>
            private GlobalQueueSegment _deqSegment;

            /// <summary>
            /// Initializes a new instance of the <see cref="GlobalQueue"/> class.
            /// </summary>
            internal GlobalQueue()
            {
                _crossSegmentLock = new object();
                _enqSegment = _deqSegment = new GlobalQueueSegment(InitialSegmentLength);
            }

            /// <summary>
            /// Adds an object to the end of the <see cref="LocalQueue"/>
            /// </summary>
            internal void Enqueue(IThreadPoolWorkItem item)
            {
                var currentSegment = _enqSegment;
                // try enqueuing. Should normally succeed unless we need a new segment.
                if (!currentSegment.TryEnqueue(item))
                {
                    // If we're unable to, we need to take a slow path that will
                    // try to add a new tail segment.
                    EnqueueSlow(currentSegment, item);
                }
            }

            /// <summary>Adds to the end of the queue, adding a new segment if necessary.</summary>
            private void EnqueueSlow(GlobalQueueSegment currentSegment, IThreadPoolWorkItem item)
            {
                for (; ; )
                {
                    currentSegment = EnsureNextSegment(currentSegment);
                    if (currentSegment.TryEnqueue(item))
                    {
                        return;
                    }
                }
            }

            private GlobalQueueSegment EnsureNextSegment(GlobalQueueSegment currentSegment)
            {
                var nextSegment = currentSegment._nextSegment;
                if (nextSegment != null)
                {
                    return nextSegment;
                }

                // If we were unsuccessful, take the lock so that we can compare and manipulate
                // the tail.  Assuming another enqueuer hasn't already added a new segment,
                // do so, then loop around to try enqueueing again.
                lock (_crossSegmentLock)
                {
                    if (currentSegment._nextSegment == null)
                    {
                        // We determine the new segment's length based on the old length.
                        // In general, we double the size of the segment, to make it less likely
                        // that we'll need to grow again.  
                        int nextSize = Math.Min(currentSegment._slots.Length * 2, MaxSegmentLength);
                        var newEnq = new GlobalQueueSegment(nextSize);

                        // Hook up the new enqueue segment.
                        currentSegment._nextSegment = newEnq;
                        _enqSegment = newEnq;
                    }
                }

                return currentSegment._nextSegment;
            }

            /// <summary>
            /// Removes an object at the beginning of the <see cref="LocalQueue"/>
            /// Returns null if the queue is empty.
            /// </summary>
            internal IThreadPoolWorkItem Dequeue()
            {
                var currentSegment = _deqSegment;
                IThreadPoolWorkItem result = currentSegment.TryDequeue();

                if (result == null && currentSegment._nextSegment != null)
                {
                    // slow path that fixes up segments
                    result = TryDequeueSlow(currentSegment);
                }

                return result;
            }

            /// <summary>
            /// Tries to dequeue an item, removing empty segments as needed.
            /// </summary>
            private IThreadPoolWorkItem TryDequeueSlow(GlobalQueueSegment currentSegment)
            {
                IThreadPoolWorkItem result;
                for (; ; )
                {
                    // At this point we know that this segment has been frozen for additional enqueues. But between
                    // the time that we ran TryDequeue and checked for a next segment,
                    // another item could have been added.  Try to dequeue one more time
                    // to confirm that the segment is indeed empty.
                    Debug.Assert(currentSegment._nextSegment != null);
                    result = currentSegment.TryDequeue();
                    if (result != null)
                    {
                        return result;
                    }

                    // Current segment is frozen (nothing more can be added) and empty (nothing is in it).
                    // Update head to point to the next segment in the list, assuming no one's beat us to it.
                    lock (_crossSegmentLock)
                    {
                        if (currentSegment == _deqSegment)
                        {
                            _deqSegment = currentSegment._nextSegment;
                        }
                    }

                    // Get the current head
                    currentSegment = _deqSegment;

                    // Try to take.  If we're successful, we're done.
                    result = currentSegment.TryDequeue();
                    if (result != null)
                    {
                        return result;
                    }

                    // Check to see whether this segment is the last. If it is, we can consider
                    // this to be a moment-in-time when the queue is empty.
                    if (currentSegment._nextSegment == null)
                    {
                        return null;
                    }
                }
            }

            /// <summary>
            /// Returns true if an item can be dequeued.
            /// There are no gurantees, obviously, since the queue may concurrently change. Just a cheap check.
            /// </summary>
            internal bool CanSteal
            {
                get
                {
                    var deqSegment = this._deqSegment;
                    return deqSegment.CanSteal;
                }
            }

            /// <summary>
            /// Provides a multi-producer, multi-consumer thread-safe bounded segment.  When the queue is full,
            /// enqueues fail and return false.  When the queue is empty, dequeues fail and return null.
            /// These segments are linked together to form the unbounded <see cref="LocalQueue"/>. 
            /// </summary>
            internal sealed class GlobalQueueSegment : QueueSegmentBase
            {
                /// <summary>The segment following this one in the queue, or null if this segment is the last in the queue.</summary>
                internal GlobalQueueSegment _nextSegment;

                /// <summary>Creates the segment.</summary>
                /// <param name="boundedLength">
                /// The maximum number of elements the segment can contain.  Must be a power of 2.
                /// </param>
                internal GlobalQueueSegment(int boundedLength) : base(boundedLength) { }

                /// <summary>
                /// Attempts to enqueue the item.  If successful, the item will be stored
                /// in the queue and true will be returned; otherwise, the item won't be stored, and false
                /// will be returned.
                /// </summary>
                public bool TryEnqueue(IThreadPoolWorkItem item)
                {
                    // Loop in case of contention...
                    var spinner = new SpinWait();
                    for (; ; )
                    {
                        var slots = _slots;
                        var slotsMask = _slotsMask;

                        // IMPORTANT: hot path. 
                        //            get from fetching Equeue to the following CmpExch as fast as possible. 
                        int position = _queueEnds.Enqueue;
                        ref Slot slot = ref GetSlot(slots, slotsMask, position);

                        // Read the sequence number for the enqueue position.
                        int sequenceNumber = Volatile.Read(ref slot.SequenceNumber);

                        // The slot is empty and ready for us to enqueue into it if its sequence
                        // number matches the slot.
                        if (sequenceNumber == position)
                        {
                            // Reserve the slot for Enqueuing.
                            //
                            // WARNING:
                            // The next few lines are not reliable on a runtime that
                            // supports thread aborts. If a thread abort were to sneak in after the CompareExchange
                            // but before the write to SequenceNumber, anyone trying to modify this slot would
                            // spin indefinitely.  If this implementation is ever used on such a platform, this
                            // if block should be wrapped in a finally / prepared region.
                            if (Interlocked.CompareExchange(ref _queueEnds.Enqueue, position + 1, position) == position)
                            {
                                // Successfully reserved the slot.  Note that after the above CompareExchange, other threads
                                // trying to return will end up spinning until we do the subsequent Write.                               
                                slot.Item = item;
                                Volatile.Write(ref slot.SequenceNumber, position + Full);
                                return true;
                            }
                        }
                        else if (sequenceNumber < position)
                        {
                            // The sequence number was less than what we needed, which means we have caught up with previous generation
                            // Technically it's possible that we have dequeuers in progress and spaces are or about to be available. 
                            // We still would be better off with a new segment.
                            EnsureFrozenForEnqueues();
                            return false;
                        }

                        // Lost a race. Spin a bit, then try again.
                        spinner.SpinOnce();
                    }
                }

                internal void EnsureFrozenForEnqueues()
                {
                    // flag used to ensure we don't increase the Tail more than once if frozen more than once
                    if (!_frozenForEnqueues)
                    {
                        lock (this)
                        {
                            // Increase the tail by FreezeOffset, spinning until we're successful in doing so.
                            var spinner = new SpinWait();
                            for (; ; )
                            {
                                int enqueue = _queueEnds.Enqueue;
                                if (Interlocked.CompareExchange(ref _queueEnds.Enqueue, enqueue + FreezeOffset, enqueue) == enqueue)
                                {
                                    break;
                                }
                                spinner.SpinOnce();
                            }

                            _frozenForEnqueues = true;
                        }
                    }
                }

                /// <summary>Tries to dequeue an element from the queue.</summary>
                internal IThreadPoolWorkItem TryDequeue()
                {
                    // Loop in case of contention...
                    var spinner = new SpinWait();

                    for (; ; )
                    {
                        var slots = _slots;
                        var slotsMask = _slotsMask;

                        // Get the dequeue position.
                        // IMPORTANT: hot path. 
                        //            get from fetching Dequeue to the following CmpExch as fast as possible. 
                        //            Lest we will clash with other dequers.
                        int position = _queueEnds.Dequeue;
                        ref Slot slot = ref GetSlot(slots, slotsMask, position);

                        // Read the sequence number for the tail position.
                        int sequenceNumber = Volatile.Read(ref slot.SequenceNumber);

                        // Check if the slot is considered Full in the current generation.
                        if (sequenceNumber == position + Full)
                        {
                            // Reserve the slot for Dequeuing.
                            //
                            // WARNING:
                            // The next few lines are not reliable on a runtime that
                            // supports thread aborts. If a thread abort were to sneak in after the CompareExchange
                            // but before the write to SequenceNumber, anyone trying to modify this slot would
                            // spin indefinitely.  If this implementation is ever used on such a platform, this
                            // if block should be wrapped in a finally / prepared region.
                            if (Interlocked.CompareExchange(ref _queueEnds.Dequeue, position + 1, position) == position)
                            {
                                // Successfully reserved the slot.  Note that after the above CompareExchange, other threads
                                // trying to enqeue will end up spinning until we do the subsequent Write.
                                var item = slot.Item;
                                slot.Item = null;

                                // make the slot appear empty in the next generation
                                Volatile.Write(ref slot.SequenceNumber, position + Empty + slots.Length);
                                return item;
                            }
                        }
                        else if (sequenceNumber < position + Full)
                        {
                            return null;
                        }

                        // Lost a race. Spin a bit, then try again.
                        spinner.SpinOnce();
                    }
                }
            }
        }

        internal class LocalQueue : WorkQueueBase
        {
            /// <summary>The current enqueue segment.</summary>
            internal LocalQueueSegment _enqSegment;
            /// <summary>The current dequeue segment.</summary>
            internal LocalQueueSegment _deqSegment;

            // TODO: VS used for debugging. may remove later.
            internal int _ID;

            internal uint _rnd = 6247;

            // Very cheap random sequence generator.
            // We do not need a lot of randomness, I think even _rnd++ would be fairly good here. 
            // Sequences attached to different queues go out of sync quickly and that is often sufficient.
            // However this sequence is a bit more random at comparable cost.
            // http://www.drdobbs.com/tools/fast-high-quality-parallel-random-number
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            internal uint NextRnd()
            {
                var r = _rnd;
                r -= (r << 21) | (r >> 11);
                return _rnd = r;
            }

            /// <summary>
            /// Initializes a new instance of the <see cref="LocalQueue"/> class.
            /// </summary>
            internal LocalQueue(int ID)
            {
                _ID = ID;
                _crossSegmentLock = new object();
                _enqSegment = _deqSegment = new LocalQueueSegment(InitialSegmentLength);
            }

            /// <summary>
            /// Adds an object to the end of the <see cref="LocalQueue"/>
            /// </summary>
            internal void Enqueue(IThreadPoolWorkItem item)
            {
                var currentSegment = _enqSegment;
                // try enqueuing. Should normally succeed unless we need a new segment.
                if (!currentSegment.TryEnqueue(item))
                {
                    // If we're unable to, we need to take a slow path that will
                    // try to add a new tail segment.
                    EnqueueSlow(currentSegment, item);
                }
            }

            /// <summary>Adds to the end of the queue, adding a new segment if necessary.</summary>
            private void EnqueueSlow(LocalQueueSegment currentSegment, IThreadPoolWorkItem item)
            {
                for (; ; )
                {
                    currentSegment = EnsureNextSegment(currentSegment);
                    if (currentSegment.TryEnqueue(item))
                    {
                        return;
                    }
                }
            }

            private LocalQueueSegment EnsureNextSegment(LocalQueueSegment currentSegment)
            {
                var nextSegment = currentSegment._nextSegment;
                if (nextSegment != null)
                {
                    return nextSegment;
                }

                // If we were unsuccessful, take the lock so that we can compare and manipulate
                // the tail.  Assuming another enqueuer hasn't already added a new segment,
                // do so, then loop around to try enqueueing again.
                lock (_crossSegmentLock)
                {
                    if (currentSegment._nextSegment == null)
                    {
                        // We determine the new segment's length based on the old length.
                        // In general, we double the size of the segment, to make it less likely
                        // that we'll need to grow again.  
                        int nextSize = Math.Min(currentSegment._slots.Length * 2, MaxSegmentLength);
                        var newEnq = new LocalQueueSegment(nextSize);

                        // Hook up the new enqueue segment.
                        currentSegment._nextSegment = newEnq;
                        _enqSegment = newEnq;
                    }
                }

                return currentSegment._nextSegment;
            }

            /// <summary>
            /// Removes an object at the beginning of the <see cref="LocalQueue"/>
            /// Returns null if the queue is empty.
            /// </summary>
            internal virtual IThreadPoolWorkItem Dequeue()
            {
                var currentSegment = _deqSegment;
                IThreadPoolWorkItem result = currentSegment.TryDequeue();

                if (result == null && currentSegment._nextSegment != null)
                {
                    // slow path that fixes up segments
                    result = TryDequeueSlow(currentSegment);
                }

                return result;
            }

            /// <summary>
            /// Tries to dequeue an item, removing empty segments as needed.
            /// </summary>
            private IThreadPoolWorkItem TryDequeueSlow(LocalQueueSegment currentSegment)
            {
                IThreadPoolWorkItem result;
                for (; ; )
                {
                    // At this point we know that this segment has been frozen for additional enqueues. But between
                    // the time that we ran TryDequeue and checked for a next segment,
                    // another item could have been added.  Try to dequeue one more time
                    // to confirm that the segment is indeed empty.
                    Debug.Assert(currentSegment._nextSegment != null);
                    result = currentSegment.TryDequeue(forceSpin: true);
                    if (result != null)
                    {
                        return result;
                    }

                    // Current segment is frozen (nothing more can be added) and empty (nothing is in it).
                    // Update head to point to the next segment in the list, assuming no one's beat us to it.
                    lock (_crossSegmentLock)
                    {
                        if (currentSegment == _deqSegment)
                        {
                            _deqSegment = currentSegment._nextSegment;
                        }
                    }

                    // Get the current head
                    currentSegment = _deqSegment;

                    // Try to take.  If we're successful, we're done.
                    result = currentSegment.TryDequeue();
                    if (result != null)
                    {
                        return result;
                    }

                    // Check to see whether this segment is the last. If it is, we can consider
                    // this to be a moment-in-time when the queue is empty.
                    if (currentSegment._nextSegment == null)
                    {
                        return null;
                    }
                }
            }

            /// <summary>
            /// Returns true if an item can be dequeued.
            /// There are no gurantees, obviously, since the queue may concurrently change. Just a cheap check.
            /// </summary>
            internal bool CanSteal
            {
                get
                {
                    var deqSegment = this._deqSegment;
                    return deqSegment.CanSteal;
                }
            }

            internal IThreadPoolWorkItem TryPop()
            {
                return this._enqSegment.TryPop();
            }

            internal bool LocalFindAndPop(IThreadPoolWorkItem callback)
            {
                return this._enqSegment.TryRemove(callback);
            }

            /// <summary>
            /// Provides a multi-producer, multi-consumer thread-safe bounded segment.  When the queue is full,
            /// enqueues fail and return false.  When the queue is empty, dequeues fail and return null.
            /// These segments are linked together to form the unbounded <see cref="LocalQueue"/>. 
            /// </summary>
            internal sealed class LocalQueueSegment : QueueSegmentBase
            {
                /// <summary>The segment following this one in the queue, or null if this segment is the last in the queue.</summary>
                internal LocalQueueSegment _nextSegment;

                private const int Change = 2;

                private const int RemoveRange = 1024;

                /// <summary>Creates the segment.</summary>
                /// <param name="boundedLength">
                /// The maximum number of elements the segment can contain.  Must be a power of 2.
                /// </param>
                internal LocalQueueSegment(int boundedLength) : base(boundedLength) { }

                /// <summary>
                /// Attempts to enqueue the item.  If successful, the item will be stored
                /// in the queue and true will be returned; otherwise, the item won't be stored, and false
                /// will be returned.
                /// </summary>
                internal bool TryEnqueue(IThreadPoolWorkItem item)
                {
                    // Loop in case of contention...
                    var spinner = new SpinWait();

                    for (; ; )
                    {
                        var slots = _slots;
                        var slotsMask = _slotsMask;

                        // IMPORTANT: hot path. 
                        //            get from fetching Equeue to the following CmpExch as fast as possible. 
                        //            Lest we will clash with other enqueuers (only an issue when this is a global queue).
                        int position = _queueEnds.Enqueue;
                        ref Slot prevSlot = ref GetSlot(slots, slotsMask, position - 1);

                        int prevSequenceNumber = prevSlot.SequenceNumber;

                        // check if prev slot is empty in the next generation or full
                        // otherwise retry - we have some kind of race, most likely the prev item is being dequeued
                        if (prevSequenceNumber == position + slotsMask | prevSequenceNumber == position)
                        {
                            if (Interlocked.CompareExchange(ref prevSlot.SequenceNumber, prevSequenceNumber + Change, prevSequenceNumber) == prevSequenceNumber)
                            {
                                // Successfully locked prev slot.
                                // Read the sequence number for the enqueue position.
                                ref Slot slot = ref GetSlot(slots, slotsMask, position);
                                int sequenceNumber = Volatile.Read(ref slot.SequenceNumber);

                                // The slot is empty and ready for us to enqueue into it.
                                // NB: it cannot become full, since slot to the left would need to be locked
                                if (sequenceNumber == position & !_frozenForEnqueues)
                                {
                                    slot.Item = item;

                                    // advance enq
                                    // NB: not interlocked since we have locked the slot
                                    _queueEnds.Enqueue = position + 1;

                                    // mark slot as full 
                                    // this enables the slot for dequeuing
                                    // NB: volatile since we need a fence after item and Enqueue stores
                                    Volatile.Write(ref slot.SequenceNumber, position + Full);

                                    // unlock prev slot, we are done
                                    prevSlot.SequenceNumber = prevSequenceNumber;
                                    return true;
                                }

                                if (position - sequenceNumber > 0)
                                {
                                    // The sequence number was less than what we needed, which means we have caught up with previous generation
                                    // Technically it's possible that we have dequeuers in progress and spaces are or about to be available. 
                                    // We still would be better off with a new segment.
                                    //
                                    // set Enqueue to throw off anyone else trying to enqueue or pop
                                    // this guarantees that Enqueue will not change and no more items will be added.
                                    _queueEnds.Enqueue = position + FreezeOffset;
                                    // NB: need a fence after Enqueue store.
                                    Volatile.Write(ref _frozenForEnqueues, true);
                                }

                                // unlock prev slot
                                prevSlot.SequenceNumber = prevSequenceNumber;
                            }
                        }

                        if (_frozenForEnqueues)
                        {
                            return false;
                        }

                        // Lost a race. Spin a bit, then try again. 
                        spinner.SpinOnce();
                    }
                }

                internal IThreadPoolWorkItem TryPop()
                {
                    IThreadPoolWorkItem item = null;

                tryAgain:
                    int position = _queueEnds.Enqueue - 1;
                    ref Slot slot = ref GetSlot(_slots, _slotsMask, position);

                    // Read the sequence number for the cell.
                    int sequenceNumber = slot.SequenceNumber;

                    // Check if the slot is considered Full in the current generation.
                    if (sequenceNumber == position + Full)
                    {
                        // Reserve the slot.
                        //
                        // WARNING:
                        // The next few lines are not reliable on a runtime that
                        // supports thread aborts. If a thread abort were to sneak in after the CompareExchange
                        // but before the write to SequenceNumber, anyone trying to modify this slot would
                        // spin indefinitely.  If this implementation is ever used on such a platform, this
                        // if block should be wrapped in a finally / prepared region.
                        if (Interlocked.CompareExchange(ref slot.SequenceNumber, position + Change, sequenceNumber) == sequenceNumber)
                        {
                            // check if enqueue changed while we were locking the slot
                            // if (_queueEnds.Enqueue - 1 == position)
                            if (_queueEnds.Enqueue == sequenceNumber)
                            {
                                item = slot.Item;
                                slot.Item = null;

                                // advance enq
                                // NB: not interlocked since we have locked the slot
                                _queueEnds.Enqueue = position;

                                // make the slot appear empty in the current generation.
                                // that also unlocks the slot
                                // NB: must be after storing to the item and advancing enq
                                Volatile.Write(ref slot.SequenceNumber, position);

                                if (item == null)
                                {
                                    // item was removed
                                    // this is not a race though, try again
                                    goto tryAgain;
                                }
                            }
                            else
                            {
                                // enque changed, some kind of contention
                                // unlock the slot and exit
                                slot.SequenceNumber = sequenceNumber;
                            }
                        }
                    }

                    // no items or contention (most likely with a dequeuer) - just return.
                    return item;
                }

                /// <summary>Tries to dequeue an element from the queue.</summary>
                internal IThreadPoolWorkItem TryDequeue(bool forceSpin = false)
                {
                    // Loop in case of contention...
                    var spinner = new SpinWait();

                    for (; ; )
                    {
                        // we may need this later. Read it now.
                        var generationGap = _slots.Length;

                        // Get the dequeue position.
                        // IMPORTANT: hot path. 
                        //            get from fetching Dequeue to the following Volatile.Write as fast as possible. 
                        //            Lest we will clash with others.
                        int position = _queueEnds.Dequeue;
                        ref Slot slot = ref GetSlot(_slots, _slotsMask, position);

                        // Read the sequence number for the cell.
                        int sequenceNumber = slot.SequenceNumber;

                        // Check if the slot is considered Full in the current generation.
                        if (sequenceNumber == position + Full)
                        {
                            // Reserve the slot for Dequeuing.
                            //
                            // WARNING:
                            // The next few lines are not reliable on a runtime that
                            // supports thread aborts. If a thread abort were to sneak in after the CompareExchange
                            // but before the write to SequenceNumber, anyone trying to modify this slot would
                            // spin indefinitely.  If this implementation is ever used on such a platform, this
                            // if block should be wrapped in a finally / prepared region.
                            if (Interlocked.CompareExchange(ref slot.SequenceNumber, position + Change, sequenceNumber) == sequenceNumber)
                            {
                                // Successfully reserved the slot.  Note that after the above CompareExchange, other threads
                                // trying to enqeue will end up spinning until we do the subsequent Write.
                                // we are committed to dequeue it

                                // NB: not interlocked since we have locked the slot
                                // doing this first so that dequers would start using the next slot. this one is ours
                                _queueEnds.Dequeue = position + 1;
                                var item = slot.Item;
                                slot.Item = null;

                                // make the slot appear empty in the next generation
                                // unlocks the slot for enqueuing
                                Volatile.Write(ref slot.SequenceNumber, position + generationGap);

                                if (item == null)
                                {
                                    // the item was removed, so we have nothing to return. 
                                    // this is not a race though. just continue.
                                    spinner.Reset();
                                    continue; 
                                }
                                return item;
                            }
                        }
                        else if (!forceSpin || position == sequenceNumber)
                        {
                            // reached empty
                            // since full slots are contiguous, finding an empty slot means that 
                            // at the point when our dequeue value was set the queue was empty 
                            // or all the values present at the time have been popped.
                            break;
                        }

                        // Lost a race, but must ensure the queue is empty before giving up.  
                        // Spin a bit, then try again.
                        spinner.SpinOnce();
                    }

                    // "null" means that:
                    // - all items enqueued at the moment of invocation (or more precisely reading deq) are gone or
                    // - we had a contention and forceSpin was not set
                    return null;
                }

                internal bool TryRemove(IThreadPoolWorkItem callback)
                {
                    for (int position = _queueEnds.Enqueue - 1, l = position - RemoveRange; position != l; position--)
                    {
                        ref Slot slot = ref GetSlot(_slots, _slotsMask, position);

                        if (slot.Item == callback)
                        {
                            // Reserve the slot.
                            //
                            // WARNING:
                            // The next few lines are not reliable on a runtime that
                            // supports thread aborts. If a thread abort were to sneak in after the CompareExchange
                            // but before the write to SequenceNumber, anyone trying to modify this slot would
                            // spin indefinitely.  If this implementation is ever used on such a platform, this
                            // if block should be wrapped in a finally / prepared region.
                            var fullSlot = position + Full;
                            if (Interlocked.CompareExchange(ref slot.SequenceNumber, position + Change, fullSlot) == fullSlot)
                            {
                                // Successfully locked the slot. 
                                // check if the item is still there 
                                if (slot.Item == callback)
                                {
                                    slot.Item = null;

                                    // unlock the slot
                                    // NB: must be after erasing the item
                                    Volatile.Write(ref slot.SequenceNumber, fullSlot);
                                    return true;
                                }

                                // unlock the slot and exit
                                slot.SequenceNumber = fullSlot;
                            }

                            // lost the item to someone else
                            break;
                        }
                        else if(slot.SequenceNumber > position + Change)
                        {
                            // reached next gen
                            break;
                        }
                    }
                    return false;
                }
            }
        }

        internal LocalQueue[] localQueues;
        internal readonly GlobalQueue globalQueue = new GlobalQueue();

        internal bool loggingEnabled;

        private Internal.PaddingFor32 pad1;
        private int numOutstandingThreadRequests = 0;
        private Internal.PaddingFor32 pad2;

        internal ThreadPoolWorkQueue()
        {
            loggingEnabled = FrameworkEventSource.Log.IsEnabled(EventLevel.Verbose, FrameworkEventSource.Keywords.ThreadPool | FrameworkEventSource.Keywords.ThreadTransfer);

            localQueues = new LocalQueue[RoundUpToPowerOf2(ThreadPoolGlobals.processorCount)];
        }

        /// <summary>
        /// Round the specified value up to the next power of 2, if it isn't one already.
        /// </summary>
        private static int RoundUpToPowerOf2(int i)
        {
            // Based on https://graphics.stanford.edu/~seander/bithacks.html#RoundUpPowerOf2
            --i;
            i |= i >> 1;
            i |= i >> 2;
            i |= i >> 4;
            i |= i >> 8;
            i |= i >> 16;
            return i + 1;
        }

        /// <summary>
        /// Returns a local queue softly affinitized with the current thread.
        /// </summary>
        internal LocalQueue GetLocalQueue()
        {
            return localQueues[GetLocalQueueIndex()];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal LocalQueue GetOrAddLocalQueue()
        {
            var index = GetLocalQueueIndex();
            var result = localQueues[index];

            if (result == null)
            {
                var newQueue = new LocalQueue(index);
                Interlocked.CompareExchange(ref localQueues[index], newQueue, null);
                result = localQueues[index];
            }

            return result;
        }

        internal int GetLocalQueueIndex()
        {
            return Threading.Thread.GetCurrentProcessorId() & (localQueues.Length - 1);
        }
        
        internal void EnsureThreadRequested()
        {
            //
            // If we have not yet requested #procs threads from the VM, then request a new thread
            // as needed
            //
            // Note that there is a separate count in the VM which will also be incremented in this case, 
            // which is handled by RequestWorkerThread.
            //
            int count = numOutstandingThreadRequests;
            while (count == 0)
            {
                int prev = Interlocked.CompareExchange(ref numOutstandingThreadRequests, 1, 0);
                if (prev == count)
                {
                    ThreadPool.RequestWorkerThread();
                    break;
                }
                count = prev;
            }
        }

        internal void RequestThread()
        {
            //
            // If we have not yet requested #procs threads from the VM, then request a new thread
            // as needed
            //
            // Note that there is a separate count in the VM which will also be incremented in this case, 
            // which is handled by RequestWorkerThread.
            //
            int count = numOutstandingThreadRequests;
            while (count < ThreadPoolGlobals.processorCount)
            {
                int prev = Interlocked.CompareExchange(ref numOutstandingThreadRequests, count + 1, count);
                if (prev == count)
                {
                    ThreadPool.RequestWorkerThread();
                    break;
                }
                count = prev;
            }
        }

        internal void MarkThreadRequestSatisfied()
        {
            //
            // The VM has called us, so one of our outstanding thread requests has been satisfied.
            // Decrement the count so that future calls to EnsureThreadRequested will succeed.
            // Note that there is a separate count in the VM which has already been decremented by the VM
            // by the time we reach this point.
            //
            int count = numOutstandingThreadRequests;
            while (count > 0)
            {
                int prev = Interlocked.CompareExchange(ref numOutstandingThreadRequests, count - 1, count);
                if (prev == count)
                {
                    break;
                }
                count = prev;
            }
        }

        public void Enqueue(IThreadPoolWorkItem callback, bool forceGlobal)
        {
            if (loggingEnabled)
                System.Diagnostics.Tracing.FrameworkEventSource.Log.ThreadPoolEnqueueWorkObject(callback);

            if (forceGlobal)
            {
                globalQueue.Enqueue(callback);
            }
            else
            {
                GetOrAddLocalQueue().Enqueue(callback);
            }

            // make sure there is at least one worker requested
            EnsureThreadRequested();
        }

        internal bool LocalFindAndPop(IThreadPoolWorkItem callback)
        {
            return GetLocalQueue()?.LocalFindAndPop(callback) == true;
        }

        public IThreadPoolWorkItem Dequeue(ref bool missedSteal)
        {
            LocalQueue[] queues = localQueues;
            var localQueueIndex = GetLocalQueueIndex();
            LocalQueue localWsq = queues[localQueueIndex];

            // first try popping from the local queue
            IThreadPoolWorkItem callback = localWsq?.TryPop();

            // then try the global queue
            if (callback == null && globalQueue.CanSteal)
            {
                callback = globalQueue.Dequeue();
            }

            if (callback == null)
            {
                var qMask = queues.Length - 1;
                var r = 0;
                if (localWsq != null)
                {
                    r = (int)localWsq.NextRnd() & qMask;
                }

                // finally try stealing from all local queues
                // Traverse all local queues starting with those that differ in lower bits and going gradually up.
                // This way we want to minimize chances that two threads concurrently go through the same sequence of queues.
                for (int i = 0; i <= qMask; i++)
                {
                    localWsq = queues[r ^ i];
                    if (localWsq?.CanSteal == true)
                    {
                        callback = localWsq.Dequeue();
                        if (callback != null)
                        {
                            break;
                        }

                        // missedSteal = missedSteal || localWsq.CanSteal;
                    }
                }
            }

            return callback;
        }

        internal static bool Dispatch()
        {
            var workQueue = ThreadPoolGlobals.workQueue;
            //
            // The clock is ticking!  We have ThreadPoolGlobals.TP_QUANTUM milliseconds to get some work done, and then
            // we need to return to the VM.
            //
            int quantumStartTime = Environment.TickCount;

            //
            // Update our records to indicate that an outstanding request for a thread has now been fulfilled.
            // From this point on, we are responsible for requesting another thread if we stop working for any
            // reason, and we believe there might still be work in the queue.
            //
            // Note that if this thread is aborted before we get a chance to request another one, the VM will
            // record a thread request on our behalf.  So we don't need to worry about getting aborted right here.
            //
            workQueue.MarkThreadRequestSatisfied();

            // Has the desire for logging changed since the last time we entered?
            workQueue.loggingEnabled = FrameworkEventSource.Log.IsEnabled(EventLevel.Verbose, FrameworkEventSource.Keywords.ThreadPool | FrameworkEventSource.Keywords.ThreadTransfer);

            //
            // Assume that we're going to need another thread if this one returns to the VM.  We'll set this to 
            // false later, but only if we're absolutely certain that the queue is empty.
            //
            bool needAnotherThread = true;
            IThreadPoolWorkItem workItem = null;

            // On first successfull Dequeue we ask for another thread.
            // If the new thread that comes is sucessful, it will ask for more and so on...
            //
            // We will repeat the request on every time quantum onwards + after some number of sucesses. As long as we find work to do.
            // If a thread finds no work due to shortage or contentions, it will exit to VM and park there.
            //
            int dequeuediItems = 0;

            Threading.Thread.RefreshCurrentProcessorId();

            try
            {
                //
                // Loop until our quantum expires.
                //
                do
                {
                    bool missedSteal = false;
                    workItem = workQueue.Dequeue(ref missedSteal);

                    if (workItem == null)
                    {
                        //
                        // No work.
                        //
                        needAnotherThread = missedSteal;

                        // Tell the VM we're returning normally, not because Hill Climbing asked us to return.
                        return true;
                    }

                    if (workQueue.loggingEnabled)
                        System.Diagnostics.Tracing.FrameworkEventSource.Log.ThreadPoolDequeueWorkObject(workItem);

                    //
                    // If we found work, there may be more work.  Ask for another thread so that the other work can be processed
                    // in parallel.  Note that this will only ask for a max of #procs threads, so it's safe to call it for every dequeue.
                    //

                    // TODO: VS this seems useless. 
                    //       is there any scenario sensitive to aggressive ramp up?
                    //       we are not very sensitive to this, just ask for a thread once in a while
                    if ((dequeuediItems++ & 31) == 0)
                    {
                        workQueue.RequestThread();
                    }

                    //
                    // Execute the workitem outside of any finally blocks, so that it can be aborted if needed.
                    //
                    if (ThreadPoolGlobals.enableWorkerTracking)
                    {
                        bool reportedStatus = false;
                        try
                        {
                            ThreadPool.ReportThreadStatus(isWorking: true);
                            reportedStatus = true;
                            workItem.ExecuteWorkItem();
                        }
                        finally
                        {
                            if (reportedStatus)
                                ThreadPool.ReportThreadStatus(isWorking: false);
                        }
                    }
                    else
                    {
                        workItem.ExecuteWorkItem();
                    }
                    workItem = null;

                    // 
                    // Notify the VM that we executed this workitem.  This is also our opportunity to ask whether Hill Climbing wants
                    // us to return the thread to the pool or not.
                    //
                    if (!ThreadPool.NotifyWorkItemComplete())
                        return false;
                }
                while ((Environment.TickCount - quantumStartTime) < ThreadPoolGlobals.TP_QUANTUM);

                // If we get here, it's because our quantum expired.  Tell the VM we're returning normally.
                return true;
            }
            catch (ThreadAbortException tae)
            {
                //
                // This is here to catch the case where this thread is aborted between the time we exit the finally block in the dispatch
                // loop, and the time we execute the work item.  QueueUserWorkItemCallback uses this to update its accounting of whether
                // it was executed or not (in debug builds only).  Task uses this to communicate the ThreadAbortException to anyone
                // who waits for the task to complete.
                //
                workItem?.MarkAborted(tae);

                //
                // In this case, the VM is going to request another thread on our behalf.  No need to do it twice.
                //
                needAnotherThread = false;
                // throw;  //no need to explicitly rethrow a ThreadAbortException, and doing so causes allocations on amd64.
            }
            finally
            {
                //
                // If we are exiting for any reason other than that the queue is definitely empty, ask for another thread.
                // If no enqueing is happening, no new threads may be requested while there might be more work.
                //
                if (needAnotherThread)
                    workQueue.RequestThread();
            }

            // we can never reach this point, but the C# compiler doesn't know that, because it doesn't know the ThreadAbortException will be reraised above.
            Debug.Fail("Should never reach this point");
            return true;
        }
    }

    internal sealed class RegisteredWaitHandleSafe : CriticalFinalizerObject
    {
        private static IntPtr InvalidHandle => Win32Native.INVALID_HANDLE_VALUE;
        private IntPtr registeredWaitHandle = InvalidHandle;
        private WaitHandle m_internalWaitObject;
        private bool bReleaseNeeded = false;
        private volatile int m_lock = 0;

        internal IntPtr GetHandle() => registeredWaitHandle;

        internal void SetHandle(IntPtr handle)
        {
            registeredWaitHandle = handle;
        }

        internal void SetWaitObject(WaitHandle waitObject)
        {
            // needed for DangerousAddRef
            RuntimeHelpers.PrepareConstrainedRegions();

            m_internalWaitObject = waitObject;
            if (waitObject != null)
            {
                m_internalWaitObject.SafeWaitHandle.DangerousAddRef(ref bReleaseNeeded);
            }
        }

        internal bool Unregister(
             WaitHandle waitObject          // object to be notified when all callbacks to delegates have completed
             )
        {
            bool result = false;
            // needed for DangerousRelease
            RuntimeHelpers.PrepareConstrainedRegions();

            // lock(this) cannot be used reliably in Cer since thin lock could be
            // promoted to syncblock and that is not a guaranteed operation
            bool bLockTaken = false;
            do
            {
                if (Interlocked.CompareExchange(ref m_lock, 1, 0) == 0)
                {
                    bLockTaken = true;
                    try
                    {
                        if (ValidHandle())
                        {
                            result = UnregisterWaitNative(GetHandle(), waitObject == null ? null : waitObject.SafeWaitHandle);
                            if (result == true)
                            {
                                if (bReleaseNeeded)
                                {
                                    m_internalWaitObject.SafeWaitHandle.DangerousRelease();
                                    bReleaseNeeded = false;
                                }
                                // if result not true don't release/suppress here so finalizer can make another attempt
                                SetHandle(InvalidHandle);
                                m_internalWaitObject = null;
                                GC.SuppressFinalize(this);
                            }
                        }
                    }
                    finally
                    {
                        m_lock = 0;
                    }
                }
                Thread.SpinWait(1);     // yield to processor
            }
            while (!bLockTaken);

            return result;
        }

        private bool ValidHandle() =>
            registeredWaitHandle != InvalidHandle && registeredWaitHandle != IntPtr.Zero;

        ~RegisteredWaitHandleSafe()
        {
            // if the app has already unregistered the wait, there is nothing to cleanup
            // we can detect this by checking the handle. Normally, there is no race condition here
            // so no need to protect reading of handle. However, if this object gets 
            // resurrected and then someone does an unregister, it would introduce a race condition
            //
            // PrepareConstrainedRegions call not needed since finalizer already in Cer
            //
            // lock(this) cannot be used reliably even in Cer since thin lock could be
            // promoted to syncblock and that is not a guaranteed operation
            //
            // Note that we will not "spin" to get this lock.  We make only a single attempt;
            // if we can't get the lock, it means some other thread is in the middle of a call
            // to Unregister, which will do the work of the finalizer anyway.
            //
            // Further, it's actually critical that we *not* wait for the lock here, because
            // the other thread that's in the middle of Unregister may be suspended for shutdown.
            // Then, during the live-object finalization phase of shutdown, this thread would
            // end up spinning forever, as the other thread would never release the lock.
            // This will result in a "leak" of sorts (since the handle will not be cleaned up)
            // but the process is exiting anyway.
            //
            // During AD-unload, we don�t finalize live objects until all threads have been 
            // aborted out of the AD.  Since these locked regions are CERs, we won�t abort them 
            // while the lock is held.  So there should be no leak on AD-unload.
            //
            if (Interlocked.CompareExchange(ref m_lock, 1, 0) == 0)
            {
                try
                {
                    if (ValidHandle())
                    {
                        WaitHandleCleanupNative(registeredWaitHandle);
                        if (bReleaseNeeded)
                        {
                            m_internalWaitObject.SafeWaitHandle.DangerousRelease();
                            bReleaseNeeded = false;
                        }
                        SetHandle(InvalidHandle);
                        m_internalWaitObject = null;
                    }
                }
                finally
                {
                    m_lock = 0;
                }
            }
        }

        [MethodImplAttribute(MethodImplOptions.InternalCall)]
        private static extern void WaitHandleCleanupNative(IntPtr handle);

        [MethodImplAttribute(MethodImplOptions.InternalCall)]
        private static extern bool UnregisterWaitNative(IntPtr handle, SafeHandle waitObject);
    }

    public sealed class RegisteredWaitHandle : MarshalByRefObject
    {
        private readonly RegisteredWaitHandleSafe internalRegisteredWait;

        internal RegisteredWaitHandle()
        {
            internalRegisteredWait = new RegisteredWaitHandleSafe();
        }

        internal void SetHandle(IntPtr handle)
        {
            internalRegisteredWait.SetHandle(handle);
        }

        internal void SetWaitObject(WaitHandle waitObject)
        {
            internalRegisteredWait.SetWaitObject(waitObject);
        }

        // This is the only public method on this class
        public bool Unregister(
             WaitHandle waitObject          // object to be notified when all callbacks to delegates have completed
             )
        {
            return internalRegisteredWait.Unregister(waitObject);
        }
    }

    public delegate void WaitCallback(Object state);

    public delegate void WaitOrTimerCallback(Object state, bool timedOut);  // signaled or timed out

    //
    // This type is necessary because VS 2010's debugger looks for a method named _ThreadPoolWaitCallbacck.PerformWaitCallback
    // on the stack to determine if a thread is a ThreadPool thread or not.  We have a better way to do this for .NET 4.5, but
    // still need to maintain compatibility with VS 2010.  When compat with VS 2010 is no longer an issue, this type may be
    // removed.
    //
    internal static class _ThreadPoolWaitCallback
    {
        internal static bool PerformWaitCallback() => ThreadPoolWorkQueue.Dispatch();
    }

    //
    // Interface to something that can be queued to the TP.  This is implemented by 
    // QueueUserWorkItemCallback, Task, and potentially other internal types.
    // For example, SemaphoreSlim represents callbacks using its own type that
    // implements IThreadPoolWorkItem.
    //
    // If we decide to expose some of the workstealing
    // stuff, this is NOT the thing we want to expose to the public.
    //
    internal interface IThreadPoolWorkItem
    {
        void ExecuteWorkItem();
        void MarkAborted(ThreadAbortException tae);
    }

    internal abstract class QueueUserWorkItemCallbackBase : IThreadPoolWorkItem
    {
#if DEBUG
        private volatile int executed;

        ~QueueUserWorkItemCallbackBase()
        {
            Debug.Assert(
                executed != 0 || Environment.HasShutdownStarted || AppDomain.CurrentDomain.IsFinalizingForUnload(),
                "A QueueUserWorkItemCallback was never called!");
        }

        protected void MarkExecuted(bool aborted)
        {
            GC.SuppressFinalize(this);
            Debug.Assert(
                0 == Interlocked.Exchange(ref executed, 1) || aborted,
                "A QueueUserWorkItemCallback was called twice!");
        }
#endif

        void IThreadPoolWorkItem.MarkAborted(ThreadAbortException tae)
        {
#if DEBUG
            // This workitem didn't execute because we got a ThreadAbortException prior to the call to ExecuteWorkItem.
            // This counts as being executed for our purposes.
            MarkExecuted(aborted: true);
#endif
        }

        public virtual void ExecuteWorkItem()
        {
#if DEBUG
            MarkExecuted(aborted: false);
#endif
        }
    }

    internal sealed class QueueUserWorkItemCallback : QueueUserWorkItemCallbackBase
    {
        private WaitCallback _callback;
        private readonly object _state;
        private readonly ExecutionContext _context;

        internal static readonly ContextCallback s_executionContextShim = state =>
        {
            var obj = (QueueUserWorkItemCallback)state;
            WaitCallback c = obj._callback;
            Debug.Assert(c != null);
            obj._callback = null;
            c(obj._state);
        };

        internal QueueUserWorkItemCallback(WaitCallback callback, object state, ExecutionContext context)
        {
            _callback = callback;
            _state = state;
            _context = context;
        }

        public override void ExecuteWorkItem()
        {
            base.ExecuteWorkItem();
            ExecutionContext context = _context;
            if (context == null)
            {
                WaitCallback c = _callback;
                _callback = null;
                c(_state);
            }
            else
            {
                ExecutionContext.RunInternal(context, s_executionContextShim, this);
            }
        }
    }

    internal sealed class QueueUserWorkItemCallback<TState> : QueueUserWorkItemCallbackBase
    {
        private Action<TState> _callback;
        private readonly TState _state;
        private readonly ExecutionContext _context;

        internal static readonly ContextCallback s_executionContextShim = state =>
        {
            var obj = (QueueUserWorkItemCallback<TState>)state;
            Action<TState> c = obj._callback;
            Debug.Assert(c != null);
            obj._callback = null;
            c(obj._state);
        };

        internal QueueUserWorkItemCallback(Action<TState> callback, TState state, ExecutionContext context)
        {
            _callback = callback;
            _state = state;
            _context = context;
        }

        public override void ExecuteWorkItem()
        {
            base.ExecuteWorkItem();
            ExecutionContext context = _context;
            if (context == null)
            {
                Action<TState> c = _callback;
                _callback = null;
                c(_state);
            }
            else
            {
                ExecutionContext.RunInternal(context, s_executionContextShim, this);
            }
        }
    }

    internal sealed class QueueUserWorkItemCallbackDefaultContext : QueueUserWorkItemCallbackBase
    {
        private WaitCallback _callback;
        private readonly object _state;

        internal static readonly ContextCallback s_executionContextShim = state =>
        {
            var obj = (QueueUserWorkItemCallbackDefaultContext)state;
            WaitCallback c = obj._callback;
            Debug.Assert(c != null);
            obj._callback = null;
            c(obj._state);
        };

        internal QueueUserWorkItemCallbackDefaultContext(WaitCallback callback, object state)
        {
            _callback = callback;
            _state = state;
        }

        public override void ExecuteWorkItem()
        {
            base.ExecuteWorkItem();
            ExecutionContext.RunInternal(executionContext: null, s_executionContextShim, this); // null executionContext on RunInternal is Default context
        }
    }

    internal sealed class QueueUserWorkItemCallbackDefaultContext<TState> : QueueUserWorkItemCallbackBase
    {
        private Action<TState> _callback;
        private readonly TState _state;

        internal static readonly ContextCallback s_executionContextShim = state =>
        {
            var obj = (QueueUserWorkItemCallbackDefaultContext<TState>)state;
            Action<TState> c = obj._callback;
            Debug.Assert(c != null);
            obj._callback = null;
            c(obj._state);
        };

        internal QueueUserWorkItemCallbackDefaultContext(Action<TState> callback, TState state)
        {
            _callback = callback;
            _state = state;
        }

        public override void ExecuteWorkItem()
        {
            base.ExecuteWorkItem();
            ExecutionContext.RunInternal(executionContext: null, s_executionContextShim, this); // null executionContext on RunInternal is Default context
        }
    }

    internal class _ThreadPoolWaitOrTimerCallback
    {
        private WaitOrTimerCallback _waitOrTimerCallback;
        private ExecutionContext _executionContext;
        private Object _state;
        private static readonly ContextCallback _ccbt = new ContextCallback(WaitOrTimerCallback_Context_t);
        private static readonly ContextCallback _ccbf = new ContextCallback(WaitOrTimerCallback_Context_f);

        internal _ThreadPoolWaitOrTimerCallback(WaitOrTimerCallback waitOrTimerCallback, Object state, bool compressStack)
        {
            _waitOrTimerCallback = waitOrTimerCallback;
            _state = state;

            if (compressStack)
            {
                // capture the exection context
                _executionContext = ExecutionContext.Capture();
            }
        }

        private static void WaitOrTimerCallback_Context_t(Object state) =>
            WaitOrTimerCallback_Context(state, timedOut: true);

        private static void WaitOrTimerCallback_Context_f(Object state) =>
            WaitOrTimerCallback_Context(state, timedOut: false);

        private static void WaitOrTimerCallback_Context(Object state, bool timedOut)
        {
            _ThreadPoolWaitOrTimerCallback helper = (_ThreadPoolWaitOrTimerCallback)state;
            helper._waitOrTimerCallback(helper._state, timedOut);
        }

        // call back helper
        internal static void PerformWaitOrTimerCallback(Object state, bool timedOut)
        {
            _ThreadPoolWaitOrTimerCallback helper = (_ThreadPoolWaitOrTimerCallback)state;
            Debug.Assert(helper != null, "Null state passed to PerformWaitOrTimerCallback!");
            // call directly if it is an unsafe call OR EC flow is suppressed
            ExecutionContext context = helper._executionContext;
            if (context == null)
            {
                WaitOrTimerCallback callback = helper._waitOrTimerCallback;
                callback(helper._state, timedOut);
            }
            else
            {
                ExecutionContext.Run(context, timedOut ? _ccbt : _ccbf, helper);
            }
        }
    }

    [CLSCompliant(false)]
    public unsafe delegate void IOCompletionCallback(uint errorCode, // Error code
                                       uint numBytes, // No. of bytes transferred 
                                       NativeOverlapped* pOVERLAP // ptr to OVERLAP structure
                                       );

    public static class ThreadPool
    {
        public static bool SetMaxThreads(int workerThreads, int completionPortThreads)
        {
            return SetMaxThreadsNative(workerThreads, completionPortThreads);
        }

        public static void GetMaxThreads(out int workerThreads, out int completionPortThreads)
        {
            GetMaxThreadsNative(out workerThreads, out completionPortThreads);
        }

        public static bool SetMinThreads(int workerThreads, int completionPortThreads)
        {
            return SetMinThreadsNative(workerThreads, completionPortThreads);
        }

        public static void GetMinThreads(out int workerThreads, out int completionPortThreads)
        {
            GetMinThreadsNative(out workerThreads, out completionPortThreads);
        }

        public static void GetAvailableThreads(out int workerThreads, out int completionPortThreads)
        {
            GetAvailableThreadsNative(out workerThreads, out completionPortThreads);
        }

        [CLSCompliant(false)]
        public static RegisteredWaitHandle RegisterWaitForSingleObject(  // throws RegisterWaitException
             WaitHandle waitObject,
             WaitOrTimerCallback callBack,
             Object state,
             uint millisecondsTimeOutInterval,
             bool executeOnlyOnce    // NOTE: we do not allow other options that allow the callback to be queued as an APC
             )
        {
            return RegisterWaitForSingleObject(waitObject, callBack, state, millisecondsTimeOutInterval, executeOnlyOnce, true);
        }

        [CLSCompliant(false)]
        public static RegisteredWaitHandle UnsafeRegisterWaitForSingleObject(  // throws RegisterWaitException
             WaitHandle waitObject,
             WaitOrTimerCallback callBack,
             Object state,
             uint millisecondsTimeOutInterval,
             bool executeOnlyOnce    // NOTE: we do not allow other options that allow the callback to be queued as an APC
             )
        {
            return RegisterWaitForSingleObject(waitObject, callBack, state, millisecondsTimeOutInterval, executeOnlyOnce, false);
        }


        private static RegisteredWaitHandle RegisterWaitForSingleObject(  // throws RegisterWaitException
             WaitHandle waitObject,
             WaitOrTimerCallback callBack,
             Object state,
             uint millisecondsTimeOutInterval,
             bool executeOnlyOnce,   // NOTE: we do not allow other options that allow the callback to be queued as an APC
             bool compressStack
             )
        {
            RegisteredWaitHandle registeredWaitHandle = new RegisteredWaitHandle();

            if (callBack != null)
            {
                _ThreadPoolWaitOrTimerCallback callBackHelper = new _ThreadPoolWaitOrTimerCallback(callBack, state, compressStack);
                state = (Object)callBackHelper;
                // call SetWaitObject before native call so that waitObject won't be closed before threadpoolmgr registration
                // this could occur if callback were to fire before SetWaitObject does its addref
                registeredWaitHandle.SetWaitObject(waitObject);
                IntPtr nativeRegisteredWaitHandle = RegisterWaitForSingleObjectNative(waitObject,
                                                                               state,
                                                                               millisecondsTimeOutInterval,
                                                                               executeOnlyOnce,
                                                                               registeredWaitHandle);
                registeredWaitHandle.SetHandle(nativeRegisteredWaitHandle);
            }
            else
            {
                throw new ArgumentNullException(nameof(WaitOrTimerCallback));
            }
            return registeredWaitHandle;
        }


        public static RegisteredWaitHandle RegisterWaitForSingleObject(  // throws RegisterWaitException
             WaitHandle waitObject,
             WaitOrTimerCallback callBack,
             Object state,
             int millisecondsTimeOutInterval,
             bool executeOnlyOnce    // NOTE: we do not allow other options that allow the callback to be queued as an APC
             )
        {
            if (millisecondsTimeOutInterval < -1)
                throw new ArgumentOutOfRangeException(nameof(millisecondsTimeOutInterval), SR.ArgumentOutOfRange_NeedNonNegOrNegative1);
            return RegisterWaitForSingleObject(waitObject, callBack, state, (UInt32)millisecondsTimeOutInterval, executeOnlyOnce, true);
        }

        public static RegisteredWaitHandle UnsafeRegisterWaitForSingleObject(  // throws RegisterWaitException
             WaitHandle waitObject,
             WaitOrTimerCallback callBack,
             Object state,
             int millisecondsTimeOutInterval,
             bool executeOnlyOnce    // NOTE: we do not allow other options that allow the callback to be queued as an APC
             )
        {
            if (millisecondsTimeOutInterval < -1)
                throw new ArgumentOutOfRangeException(nameof(millisecondsTimeOutInterval), SR.ArgumentOutOfRange_NeedNonNegOrNegative1);
            return RegisterWaitForSingleObject(waitObject, callBack, state, (UInt32)millisecondsTimeOutInterval, executeOnlyOnce, false);
        }

        public static RegisteredWaitHandle RegisterWaitForSingleObject(  // throws RegisterWaitException
            WaitHandle waitObject,
            WaitOrTimerCallback callBack,
            Object state,
            long millisecondsTimeOutInterval,
            bool executeOnlyOnce    // NOTE: we do not allow other options that allow the callback to be queued as an APC
        )
        {
            if (millisecondsTimeOutInterval < -1)
                throw new ArgumentOutOfRangeException(nameof(millisecondsTimeOutInterval), SR.ArgumentOutOfRange_NeedNonNegOrNegative1);
            return RegisterWaitForSingleObject(waitObject, callBack, state, (UInt32)millisecondsTimeOutInterval, executeOnlyOnce, true);
        }

        public static RegisteredWaitHandle UnsafeRegisterWaitForSingleObject(  // throws RegisterWaitException
            WaitHandle waitObject,
            WaitOrTimerCallback callBack,
            Object state,
            long millisecondsTimeOutInterval,
            bool executeOnlyOnce    // NOTE: we do not allow other options that allow the callback to be queued as an APC
        )
        {
            if (millisecondsTimeOutInterval < -1)
                throw new ArgumentOutOfRangeException(nameof(millisecondsTimeOutInterval), SR.ArgumentOutOfRange_NeedNonNegOrNegative1);
            return RegisterWaitForSingleObject(waitObject, callBack, state, (UInt32)millisecondsTimeOutInterval, executeOnlyOnce, false);
        }

        public static RegisteredWaitHandle RegisterWaitForSingleObject(
                          WaitHandle waitObject,
                          WaitOrTimerCallback callBack,
                          Object state,
                          TimeSpan timeout,
                          bool executeOnlyOnce
                          )
        {
            long tm = (long)timeout.TotalMilliseconds;
            if (tm < -1)
                throw new ArgumentOutOfRangeException(nameof(timeout), SR.ArgumentOutOfRange_NeedNonNegOrNegative1);
            if (tm > (long)Int32.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(timeout), SR.ArgumentOutOfRange_LessEqualToIntegerMaxVal);
            return RegisterWaitForSingleObject(waitObject, callBack, state, (UInt32)tm, executeOnlyOnce, true);
        }

        public static RegisteredWaitHandle UnsafeRegisterWaitForSingleObject(
                          WaitHandle waitObject,
                          WaitOrTimerCallback callBack,
                          Object state,
                          TimeSpan timeout,
                          bool executeOnlyOnce
                          )
        {
            long tm = (long)timeout.TotalMilliseconds;
            if (tm < -1)
                throw new ArgumentOutOfRangeException(nameof(timeout), SR.ArgumentOutOfRange_NeedNonNegOrNegative1);
            if (tm > (long)Int32.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(timeout), SR.ArgumentOutOfRange_LessEqualToIntegerMaxVal);
            return RegisterWaitForSingleObject(waitObject, callBack, state, (UInt32)tm, executeOnlyOnce, false);
        }

        public static bool QueueUserWorkItem(WaitCallback callBack) =>
            QueueUserWorkItem(callBack, null);

        public static bool QueueUserWorkItem(WaitCallback callBack, object state)
        {
            if (callBack == null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.callBack);
            }

            EnsureVMInitialized();

            ExecutionContext context = ExecutionContext.Capture();

            IThreadPoolWorkItem tpcallBack = (context != null && context.IsDefault) ?
                new QueueUserWorkItemCallbackDefaultContext(callBack, state) :
                (IThreadPoolWorkItem)new QueueUserWorkItemCallback(callBack, state, context);

            ThreadPoolGlobals.workQueue.Enqueue(tpcallBack, forceGlobal: true);

            return true;
        }

        public static bool QueueUserWorkItem<TState>(Action<TState> callBack, TState state, bool preferLocal)
        {
            if (callBack == null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.callBack);
            }

            EnsureVMInitialized();

            ExecutionContext context = ExecutionContext.Capture();

            IThreadPoolWorkItem tpcallBack = (context != null && context.IsDefault) ?
                new QueueUserWorkItemCallbackDefaultContext<TState>(callBack, state) :
                (IThreadPoolWorkItem)new QueueUserWorkItemCallback<TState>(callBack, state, context);

            ThreadPoolGlobals.workQueue.Enqueue(tpcallBack, forceGlobal: !preferLocal);

            return true;
        }

        public static bool UnsafeQueueUserWorkItem(WaitCallback callBack, Object state)
        {
            if (callBack == null)
            {
                ThrowHelper.ThrowArgumentNullException(ExceptionArgument.callBack);
            }

            EnsureVMInitialized();

            IThreadPoolWorkItem tpcallBack = new QueueUserWorkItemCallback(callBack, state, null);

            ThreadPoolGlobals.workQueue.Enqueue(tpcallBack, forceGlobal: true);

            return true;
        }

        internal static void UnsafeQueueCustomWorkItem(IThreadPoolWorkItem workItem, bool forceGlobal)
        {
            Debug.Assert(null != workItem);
            EnsureVMInitialized();
            ThreadPoolGlobals.workQueue.Enqueue(workItem, forceGlobal);
        }

        // This method tries to take the target callback out of the current thread's queue.
        internal static bool TryPopCustomWorkItem(IThreadPoolWorkItem workItem)
        {
            Debug.Assert(null != workItem);
            return
                ThreadPoolGlobals.vmTpInitialized && // if not initialized, so there's no way this workitem was ever queued.
                ThreadPoolGlobals.workQueue.LocalFindAndPop(workItem);
        }

        // Get all workitems.  Called by TaskScheduler in its debugger hooks.
        internal static IEnumerable<IThreadPoolWorkItem> GetQueuedWorkItems()
        {
            var workQueue = ThreadPoolGlobals.workQueue;

            // Enumerate global queue
            //foreach (IThreadPoolWorkItem workItem in workQueue.workItems)
            //{
            //    yield return workItem;
            //}

            // Enumerate each local queue
            foreach (ThreadPoolWorkQueue.LocalQueue wsq in workQueue.localQueues)
            {
                for (var head = wsq._deqSegment; head != null; head = head._nextSegment)
                {
                    foreach (var slot in head._slots)
                    {
                        IThreadPoolWorkItem item = slot.Item;
                        if (item != null)
                        {
                            yield return item;
                        }
                    }
                }
            }
        }

        internal static IEnumerable<IThreadPoolWorkItem> GetLocallyQueuedWorkItems()
        {
            ThreadPoolWorkQueue.LocalQueue wsq = ThreadPoolGlobals.workQueue?.GetLocalQueue();
            for (var head = wsq._deqSegment; head != null; head = head._nextSegment)
            {
                foreach (var slot in head._slots)
                {
                    IThreadPoolWorkItem item = slot.Item;
                    if (item != null)
                    {
                        yield return item;
                    }
                }
            }
        }

        internal static IEnumerable<IThreadPoolWorkItem> GetGloballyQueuedWorkItems() => GetQueuedWorkItems(); // ThreadPoolGlobals.workQueue.workItems;

        private static object[] ToObjectArray(IEnumerable<IThreadPoolWorkItem> workitems)
        {
            int i = 0;
            foreach (IThreadPoolWorkItem item in workitems)
            {
                i++;
            }

            object[] result = new object[i];
            i = 0;
            foreach (IThreadPoolWorkItem item in workitems)
            {
                if (i < result.Length) //just in case someone calls us while the queues are in motion
                    result[i] = item;
                i++;
            }

            return result;
        }

        // This is the method the debugger will actually call, if it ends up calling
        // into ThreadPool directly.  Tests can use this to simulate a debugger, as well.
        internal static object[] GetQueuedWorkItemsForDebugger() =>
            ToObjectArray(GetQueuedWorkItems());

        internal static object[] GetGloballyQueuedWorkItemsForDebugger() =>
            ToObjectArray(GetGloballyQueuedWorkItems());

        internal static object[] GetLocallyQueuedWorkItemsForDebugger() =>
            ToObjectArray(GetLocallyQueuedWorkItems());

        [DllImport(JitHelpers.QCall, CharSet = CharSet.Unicode)]
        internal static extern bool RequestWorkerThread();

        [MethodImplAttribute(MethodImplOptions.InternalCall)]
        private static extern unsafe bool PostQueuedCompletionStatus(NativeOverlapped* overlapped);

        [CLSCompliant(false)]
        public static unsafe bool UnsafeQueueNativeOverlapped(NativeOverlapped* overlapped) =>
            PostQueuedCompletionStatus(overlapped);

        // The thread pool maintains a per-appdomain managed work queue.
        // New thread pool entries are added in the managed queue.
        // The VM is responsible for the actual growing/shrinking of 
        // threads. 
        private static void EnsureVMInitialized()
        {
            if (!ThreadPoolGlobals.vmTpInitialized)
            {
                EnsureVMInitializedCore(); // separate out to help with inlining
            }
        }

        private static void EnsureVMInitializedCore()
        {
            ThreadPool.InitializeVMTp(ref ThreadPoolGlobals.enableWorkerTracking);
            ThreadPoolGlobals.vmTpInitialized = true;
        }

        // Native methods: 

        [MethodImplAttribute(MethodImplOptions.InternalCall)]
        private static extern bool SetMinThreadsNative(int workerThreads, int completionPortThreads);

        [MethodImplAttribute(MethodImplOptions.InternalCall)]
        private static extern bool SetMaxThreadsNative(int workerThreads, int completionPortThreads);

        [MethodImplAttribute(MethodImplOptions.InternalCall)]
        private static extern void GetMinThreadsNative(out int workerThreads, out int completionPortThreads);

        [MethodImplAttribute(MethodImplOptions.InternalCall)]
        private static extern void GetMaxThreadsNative(out int workerThreads, out int completionPortThreads);

        [MethodImplAttribute(MethodImplOptions.InternalCall)]
        private static extern void GetAvailableThreadsNative(out int workerThreads, out int completionPortThreads);

        [MethodImplAttribute(MethodImplOptions.InternalCall)]
        internal static extern bool NotifyWorkItemComplete();

        [MethodImplAttribute(MethodImplOptions.InternalCall)]
        internal static extern void ReportThreadStatus(bool isWorking);

        internal static void NotifyWorkItemProgress()
        {
            if (!ThreadPoolGlobals.vmTpInitialized)
                ThreadPool.InitializeVMTp(ref ThreadPoolGlobals.enableWorkerTracking);
            NotifyWorkItemProgressNative();
        }

        [MethodImplAttribute(MethodImplOptions.InternalCall)]
        internal static extern void NotifyWorkItemProgressNative();

        [DllImport(JitHelpers.QCall, CharSet = CharSet.Unicode)]
        private static extern void InitializeVMTp(ref bool enableWorkerTracking);

        [MethodImplAttribute(MethodImplOptions.InternalCall)]
        private static extern IntPtr RegisterWaitForSingleObjectNative(
             WaitHandle waitHandle,
             Object state,
             uint timeOutInterval,
             bool executeOnlyOnce,
             RegisteredWaitHandle registeredWaitHandle
             );


        [Obsolete("ThreadPool.BindHandle(IntPtr) has been deprecated.  Please use ThreadPool.BindHandle(SafeHandle) instead.", false)]
        public static bool BindHandle(IntPtr osHandle)
        {
            return BindIOCompletionCallbackNative(osHandle);
        }

        public static bool BindHandle(SafeHandle osHandle)
        {
            if (osHandle == null)
                throw new ArgumentNullException(nameof(osHandle));

            bool ret = false;
            bool mustReleaseSafeHandle = false;
            RuntimeHelpers.PrepareConstrainedRegions();
            try
            {
                osHandle.DangerousAddRef(ref mustReleaseSafeHandle);
                ret = BindIOCompletionCallbackNative(osHandle.DangerousGetHandle());
            }
            finally
            {
                if (mustReleaseSafeHandle)
                    osHandle.DangerousRelease();
            }
            return ret;
        }

        [MethodImplAttribute(MethodImplOptions.InternalCall)]
        private static extern bool BindIOCompletionCallbackNative(IntPtr fileHandle);
    }
}

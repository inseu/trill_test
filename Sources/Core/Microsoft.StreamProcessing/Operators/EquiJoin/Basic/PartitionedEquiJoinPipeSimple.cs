﻿// *********************************************************************
// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License
// *********************************************************************
using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using Microsoft.StreamProcessing.Internal;
using Microsoft.StreamProcessing.Internal.Collections;

namespace Microsoft.StreamProcessing
{
    [DataContract]
    [KnownType(typeof(EndPointHeap))]
    [KnownType(typeof(EndPointQueue))]
    internal sealed class PartitionedEquiJoinPipeSimple<TLeft, TRight, TResult, TPartitionKey> : BinaryPipe<PartitionKey<TPartitionKey>, TLeft, TRight, TResult>, IBinaryObserver
    {
        private readonly Func<TLeft, TRight, TResult> selector;
        private readonly MemoryPool<PartitionKey<TPartitionKey>, TResult> pool;
        private readonly Func<IEndPointOrderer> endpointGenerator;

        [SchemaSerialization]
        private readonly Expression<Func<PartitionKey<TPartitionKey>, PartitionKey<TPartitionKey>, bool>> keyComparer;
        [SchemaSerialization]
        private readonly Expression<Func<TLeft, TLeft, bool>> leftComparer;
        private readonly Func<TLeft, TLeft, bool> leftComparerEquals;
        [SchemaSerialization]
        private readonly Expression<Func<TRight, TRight, bool>> rightComparer;
        private readonly Func<TRight, TRight, bool> rightComparerEquals;

        [DataMember]
        private FastDictionary2<TPartitionKey, Queue<LEntry>> leftQueue = new FastDictionary2<TPartitionKey, Queue<LEntry>>();
        [DataMember]
        private FastDictionary2<TPartitionKey, Queue<REntry>> rightQueue = new FastDictionary2<TPartitionKey, Queue<REntry>>();
        [DataMember]
        private HashSet<TPartitionKey> processQueue = new HashSet<TPartitionKey>();
        [DataMember]
        private HashSet<TPartitionKey> seenKeys = new HashSet<TPartitionKey>();
        [DataMember]
        private HashSet<TPartitionKey> cleanKeys = new HashSet<TPartitionKey>();

        [DataMember]
        private StreamMessage<PartitionKey<TPartitionKey>, TResult> output;

        [DataMember]
        private FastDictionary<TPartitionKey, PartitionEntry> partitionData = new FastDictionary<TPartitionKey, PartitionEntry>();
        [DataMember]
        private long lastLeftCTI = long.MinValue;
        [DataMember]
        private long lastRightCTI = long.MinValue;
        [DataMember]
        private bool emitCTI = false;

        [Obsolete("Used only by serialization. Do not call directly.")]
        public PartitionedEquiJoinPipeSimple() { }

        public PartitionedEquiJoinPipeSimple(
            BinaryStreamable<PartitionKey<TPartitionKey>, TLeft, TRight, TResult> stream,
            Expression<Func<TLeft, TRight, TResult>> selector,
            IStreamObserver<PartitionKey<TPartitionKey>, TResult> observer)
            : base(stream, observer)
        {
            this.selector = selector.Compile();

            this.keyComparer = stream.Properties.KeyEqualityComparer.GetEqualsExpr();

            this.leftComparer = stream.Left.Properties.PayloadEqualityComparer.GetEqualsExpr();
            this.leftComparerEquals = this.leftComparer.Compile();

            this.rightComparer = stream.Right.Properties.PayloadEqualityComparer.GetEqualsExpr();
            this.rightComparerEquals = this.rightComparer.Compile();

            if (stream.Left.Properties.IsIntervalFree && stream.Right.Properties.IsConstantDuration)
                this.endpointGenerator = () => new EndPointQueue();
            else if (stream.Right.Properties.IsIntervalFree && stream.Left.Properties.IsConstantDuration)
                this.endpointGenerator = () => new EndPointQueue();
            else if (stream.Left.Properties.IsConstantDuration && stream.Right.Properties.IsConstantDuration &&
                     stream.Left.Properties.ConstantDurationLength == stream.Right.Properties.ConstantDurationLength)
                this.endpointGenerator = () => new EndPointQueue();
            else
                this.endpointGenerator = () => new EndPointHeap();

            this.pool = MemoryManager.GetMemoryPool<PartitionKey<TPartitionKey>, TResult>(stream.Properties.IsColumnar);
            this.pool.Get(out this.output);
            this.output.Allocate();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void UpdateNextLeftTime(PartitionEntry partition, long time)
        {
            partition.nextLeftTime = time;
            if (time == StreamEvent.InfinitySyncTime
                && partition.leftEdgeMap.IsInvisibleEmpty
                && partition.leftIntervalMap.IsInvisibleEmpty
                && partition.leftIntervalMap.IsEmpty)
            {
                partition.isLeftComplete = true;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void UpdateNextRightTime(PartitionEntry partition, long time)
        {
            partition.nextRightTime = time;
            if (time == StreamEvent.InfinitySyncTime
                && partition.rightEdgeMap.IsInvisibleEmpty
                && partition.rightIntervalMap.IsInvisibleEmpty
                && partition.rightIntervalMap.IsEmpty)
            {
                partition.isRightComplete = true;
            }
        }

        protected override void ProduceBinaryQueryPlan(PlanNode left, PlanNode right)
        {
            var node = new JoinPlanNode(
                left, right, this,
                typeof(TLeft), typeof(TRight), typeof(TLeft), typeof(PartitionKey<TPartitionKey>),
                JoinKind.EquiJoin,
                false, null);
            node.AddJoinExpression("key comparer", this.keyComparer);
            node.AddJoinExpression("left key comparer", this.leftComparer);
            node.AddJoinExpression("right key comparer", this.rightComparer);
            this.Observer.ProduceQueryPlan(node);
        }

        private void NewPartition(TPartitionKey pKey, int hash)
        {
            this.leftQueue.Insert(pKey, new Queue<LEntry>());
            this.rightQueue.Insert(pKey, new Queue<REntry>());

            if (!this.partitionData.Lookup(pKey, out int index))
            {
                this.partitionData.Insert(
                    ref index,
                    pKey,
                    new PartitionEntry { endPointHeap = this.endpointGenerator(), key = pKey, hash = hash });
            }
        }

        protected override void ProcessBothBatches(StreamMessage<PartitionKey<TPartitionKey>, TLeft> leftBatch, StreamMessage<PartitionKey<TPartitionKey>, TRight> rightBatch, out bool leftBatchDone, out bool rightBatchDone, out bool leftBatchFree, out bool rightBatchFree)
        {
            ProcessLeftBatch(leftBatch, out leftBatchDone, out leftBatchFree);
            ProcessRightBatch(rightBatch, out rightBatchDone, out rightBatchFree);
        }

        protected override void ProcessLeftBatch(StreamMessage<PartitionKey<TPartitionKey>, TLeft> batch, out bool leftBatchDone, out bool leftBatchFree)
        {
            leftBatchDone = true;
            leftBatchFree = true;
            batch.iter = 0;

            Queue<LEntry> queue = null;
            TPartitionKey previous = default;
            bool first = true;
            for (var i = 0; i < batch.Count; i++)
            {
                if ((batch.bitvector.col[i >> 6] & (1L << (i & 0x3f))) != 0
                    && (batch.vother.col[i] >= 0)) continue;

                if (batch.vother.col[i] == PartitionedStreamEvent.LowWatermarkOtherTime)
                {
                    bool timeAdvanced = this.lastLeftCTI != batch.vsync.col[i];
                    if (timeAdvanced && this.lastLeftCTI < this.lastRightCTI) this.emitCTI = true;
                    this.lastLeftCTI = batch.vsync.col[i];
                    foreach (var p in this.seenKeys) this.processQueue.Add(p);
                    continue;
                }

                var partitionKey = batch.key.col[i].Key;
                if (first || !partitionKey.Equals(previous))
                {
                    if (this.seenKeys.Add(partitionKey)) NewPartition(partitionKey, batch.hash.col[i]);
                    this.leftQueue.Lookup(partitionKey, out int index);
                    queue = this.leftQueue.entries[index].value;
                    this.processQueue.Add(partitionKey);
                }
                var e = new LEntry
                {
                    Sync = batch.vsync.col[i],
                    Other = batch.vother.col[i],
                    Payload = batch.payload.col[i],
                };
                queue.Enqueue(e);
                first = false;
                previous = partitionKey;
            }

            ProcessPendingEntries();
        }

        protected override void ProcessRightBatch(StreamMessage<PartitionKey<TPartitionKey>, TRight> batch, out bool rightBatchDone, out bool rightBatchFree)
        {
            rightBatchDone = true;
            rightBatchFree = true;
            batch.iter = 0;

            Queue<REntry> queue = null;
            TPartitionKey previous = default;
            bool first = true;
            for (var i = 0; i < batch.Count; i++)
            {
                if ((batch.bitvector.col[i >> 6] & (1L << (i & 0x3f))) != 0
                    && (batch.vother.col[i] >= 0)) continue;

                if (batch.vother.col[i] == PartitionedStreamEvent.LowWatermarkOtherTime)
                {
                    bool timeAdvanced = this.lastRightCTI != batch.vsync.col[i];
                    if (timeAdvanced && this.lastRightCTI < this.lastLeftCTI) this.emitCTI = true;
                    this.lastRightCTI = batch.vsync.col[i];
                    foreach (var p in this.seenKeys) this.processQueue.Add(p);
                    continue;
                }

                var partitionKey = batch.key.col[i].Key;
                if (first || !partitionKey.Equals(previous))
                {
                    if (this.seenKeys.Add(partitionKey)) NewPartition(partitionKey, batch.hash.col[i]);
                    this.rightQueue.Lookup(partitionKey, out int index);
                    queue = this.rightQueue.entries[index].value;
                    this.processQueue.Add(partitionKey);
                }
                var e = new REntry
                {
                    Sync = batch.vsync.col[i],
                    Other = batch.vother.col[i],
                    Payload = batch.payload.col[i],
                };
                queue.Enqueue(e);
                first = false;
                previous = partitionKey;
            }

            ProcessPendingEntries();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected override void DisposeState() => this.output.Free();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ProcessPendingEntries()
        {
            foreach (var pKey in this.processQueue)
            {
                Queue<LEntry> leftWorking = null;
                Queue<REntry> rightWorking = null;

                this.leftQueue.Lookup(pKey, out int index);
                leftWorking = this.leftQueue.entries[index].value;
                rightWorking = this.rightQueue.entries[index].value;
                this.partitionData.Lookup(pKey, out index);
                var partition = this.partitionData.entries[index].value;

                while (true)
                {
                    LEntry leftEntry;
                    REntry rightEntry;
                    var old = partition.currTime;
                    bool hasLeftBatch = leftWorking.Count != 0;
                    bool hasRightBatch = rightWorking.Count != 0;
                    if (hasLeftBatch && hasRightBatch)
                    {
                        leftEntry = leftWorking.Peek();
                        rightEntry = rightWorking.Peek();
                        UpdateNextLeftTime(partition, leftEntry.Sync);
                        UpdateNextRightTime(partition, rightEntry.Sync);

                        if (partition.nextLeftTime < partition.nextRightTime)
                        {
                            UpdateTime(partition, partition.nextLeftTime);

                            if (leftEntry.Other != long.MinValue)
                            {
                                ProcessLeftEvent(
                                    partition,
                                    partition.nextLeftTime,
                                    leftEntry.Other,
                                    leftEntry.Payload);
                            }
                            else if (partition.currTime > old)
                            {
                                var r = default(TRight);
                                AddToBatch(
                                    partition.currTime,
                                    long.MinValue,
                                    partition,
                                    ref leftEntry.Payload,
                                    ref r);
                            }

                            leftWorking.Dequeue();
                        }
                        else
                        {
                            UpdateTime(partition, partition.nextRightTime);

                            if (rightEntry.Other != long.MinValue)
                            {
                                ProcessRightEvent(
                                    partition,
                                    partition.nextRightTime,
                                    rightEntry.Other,
                                    rightEntry.Payload);
                            }
                            else if (partition.currTime > old)
                            {
                                var l = default(TLeft);
                                AddToBatch(
                                    partition.currTime,
                                    long.MinValue,
                                    partition,
                                    ref l,
                                    ref rightEntry.Payload);
                            }

                            rightWorking.Dequeue();
                        }
                    }
                    else if (hasLeftBatch)
                    {
                        leftEntry = leftWorking.Peek();
                        UpdateNextLeftTime(partition, leftEntry.Sync);
                        partition.nextRightTime = Math.Max(partition.nextRightTime, this.lastRightCTI);
                        if (partition.nextLeftTime > partition.nextRightTime)
                        {
                            // If we have not yet reached the lesser of the two sides (in this case, right), and we don't
                            // have input from that side, reach that time now. This can happen with low watermarks.
                            if (partition.currTime < partition.nextRightTime)
                                UpdateTime(partition, partition.nextRightTime);
                            break;
                        }

                        UpdateTime(partition, partition.nextLeftTime);

                        if (leftEntry.Other != long.MinValue)
                        {
                            ProcessLeftEvent(
                                partition,
                                partition.nextLeftTime,
                                leftEntry.Other,
                                leftEntry.Payload);
                        }
                        else if (partition.currTime > old)
                        {
                            var r = default(TRight);
                            AddToBatch(
                                partition.currTime,
                                long.MinValue,
                                partition,
                                ref leftEntry.Payload,
                                ref r);
                        }

                        leftWorking.Dequeue();
                    }
                    else if (hasRightBatch)
                    {
                        rightEntry = rightWorking.Peek();
                        UpdateNextRightTime(partition, rightEntry.Sync);
                        partition.nextLeftTime = Math.Max(partition.nextLeftTime, this.lastLeftCTI);
                        if (partition.nextLeftTime < partition.nextRightTime)
                        {
                            // If we have not yet reached the lesser of the two sides (in this case, left), and we don't
                            // have input from that side, reach that time now. This can happen with low watermarks.
                            if (partition.currTime < partition.nextLeftTime)
                                UpdateTime(partition, partition.nextLeftTime);
                            break;
                        }

                        UpdateTime(partition, partition.nextRightTime);

                        if (rightEntry.Other != long.MinValue)
                        {
                            ProcessRightEvent(
                                partition,
                                partition.nextRightTime,
                                rightEntry.Other,
                                rightEntry.Payload);
                        }
                        else if (partition.currTime > old)
                        {
                            var l = default(TLeft);
                            AddToBatch(
                                partition.currTime,
                                long.MinValue,
                                partition,
                                ref l,
                                ref rightEntry.Payload);
                        }

                        rightWorking.Dequeue();
                    }
                    else
                    {
                        if (partition.nextLeftTime < this.lastLeftCTI)
                            UpdateNextLeftTime(partition, this.lastLeftCTI);
                        if (partition.nextRightTime < this.lastRightCTI)
                            UpdateNextRightTime(partition, this.lastRightCTI);

                        UpdateTime(partition, Math.Min(this.lastLeftCTI, this.lastRightCTI));
                        if (partition.IsClean()) this.cleanKeys.Add(pKey);

                        break;
                    }
                }
            }

            if (this.emitCTI)
            {
                var earliest = Math.Min(this.lastLeftCTI, this.lastRightCTI);
                AddLowWatermarkToBatch(earliest);
                this.emitCTI = false;
                foreach (var p in this.cleanKeys)
                {
                    this.seenKeys.Remove(p);
                    this.leftQueue.Remove(p);
                    this.rightQueue.Remove(p);
                }

                this.cleanKeys.Clear();
            }

            this.processQueue.Clear();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void UpdateTime(PartitionEntry partition, long time)
        {
            if (time > partition.currTime)
            {
                LeaveTime(partition);
                partition.currTime = time;
                ReachTime(partition);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ProcessLeftEvent(PartitionEntry partition, long start, long end, TLeft payload)
        {
            if (start < end)
            {
                // Row is a start edge or interval.
                bool processable = partition.nextRightTime > start || partition.rightEdgeMap.IsEmpty;
                if (end == StreamEvent.InfinitySyncTime)
                {
                    // Row is a start edge.
                    if (processable)
                    {
                        if (!partition.isRightComplete)
                        {
                            int index = partition.leftEdgeMap.Insert(partition.hash);
                            partition.leftEdgeMap.Values[index].Populate(start, ref payload);
                        }

                        CreateOutputForStartEdge(partition, start, ref payload);
                    }
                    else
                    {
                        int index = partition.leftEdgeMap.InsertInvisible(partition.hash);
                        partition.leftEdgeMap.Values[index].Populate(start, ref payload);
                    }
                }
                else
                {
                    // Row is an interval.
                    if (processable)
                    {
                        int index = partition.leftIntervalMap.Insert(partition.hash);
                        partition.leftIntervalMap.Values[index].Populate(start, end, ref payload);
                        CreateOutputForStartInterval(partition, start, end, ref payload);
                        partition.endPointHeap.Insert(end, index);
                    }
                    else
                    {
                        int index = partition.leftIntervalMap.InsertInvisible(partition.hash);
                        partition.leftIntervalMap.Values[index].Populate(start, end, ref payload);
                    }
                }
            }
            else
            {
                // Row is an end edge.

                // Remove from leftEdgeMap.
                if (!partition.isRightComplete)
                {
                    var edges = partition.leftEdgeMap.Find(partition.hash);
                    while (edges.Next(out int index))
                    {
                        if (AreSame(end, ref payload, ref partition.leftEdgeMap.Values[index]))
                        {
                            edges.Remove();
                            break;
                        }
                    }
                }

                // Output end edges.
                CreateOutputForEndEdge(partition, start, end, ref payload);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ProcessRightEvent(PartitionEntry partition, long start, long end, TRight payload)
        {
            if (start < end)
            {
                // Row is a start edge or interval.
                bool processable = partition.nextLeftTime > start || partition.leftEdgeMap.IsEmpty;
                if (end == StreamEvent.InfinitySyncTime)
                {
                    // Row is a start edge.
                    if (processable)
                    {
                        if (!partition.isLeftComplete)
                        {
                            int index = partition.rightEdgeMap.Insert(partition.hash);
                            partition.rightEdgeMap.Values[index].Populate(start, ref payload);
                        }

                        CreateOutputForStartEdge(partition, start, ref payload);
                    }
                    else
                    {
                        int index = partition.rightEdgeMap.InsertInvisible(partition.hash);
                        partition.rightEdgeMap.Values[index].Populate(start, ref payload);
                    }
                }
                else
                {
                    // Row is an interval.
                    if (processable)
                    {
                        int index = partition.rightIntervalMap.Insert(partition.hash);
                        partition.rightIntervalMap.Values[index].Populate(start, end, ref payload);
                        CreateOutputForStartInterval(partition, start, end, ref payload);
                        partition.endPointHeap.Insert(end, ~index);
                    }
                    else
                    {
                        int index = partition.rightIntervalMap.InsertInvisible(partition.hash);
                        partition.rightIntervalMap.Values[index].Populate(start, end, ref payload);
                    }
                }
            }
            else
            {
                // Row is an end edge.

                // Remove from leftEdgeMap.
                if (!partition.isLeftComplete)
                {
                    var edges = partition.rightEdgeMap.Find(partition.hash);
                    while (edges.Next(out int index))
                    {
                        if (AreSame(end, ref payload, ref partition.rightEdgeMap.Values[index]))
                        {
                            edges.Remove();
                            break;
                        }
                    }
                }

                // Output end edges.
                CreateOutputForEndEdge(partition, start, end, ref payload);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void LeaveTime(PartitionEntry partition)
        {
            var leftEdges = partition.leftEdgeMap.TraverseInvisible();
            while (leftEdges.Next(out int index, out _))
            {
                CreateOutputForStartEdge(
                    partition,
                    partition.currTime,
                    ref partition.leftEdgeMap.Values[index].Payload);
                leftEdges.MakeVisible();
            }

            var leftIntervals = partition.leftIntervalMap.TraverseInvisible();
            while (leftIntervals.Next(out int index, out _))
            {
                long end = partition.leftIntervalMap.Values[index].End;
                CreateOutputForStartInterval(
                    partition,
                    partition.currTime,
                    end,
                    ref partition.leftIntervalMap.Values[index].Payload);
                leftIntervals.MakeVisible();
                partition.endPointHeap.Insert(end, index);
            }

            var rightEdges = partition.rightEdgeMap.TraverseInvisible();
            while (rightEdges.Next(out int index, out _))
            {
                CreateOutputForStartEdge(
                    partition,
                    partition.currTime,
                    ref partition.rightEdgeMap.Values[index].Payload);
                rightEdges.MakeVisible();
            }

            var rightIntervals = partition.rightIntervalMap.TraverseInvisible();
            while (rightIntervals.Next(out int index, out _))
            {
                long end = partition.rightIntervalMap.Values[index].End;
                CreateOutputForStartInterval(
                    partition,
                    partition.currTime,
                    end,
                    ref partition.rightIntervalMap.Values[index].Payload);
                rightIntervals.MakeVisible();
                partition.endPointHeap.Insert(end, ~index);
            }

            if (partition.nextLeftTime == StreamEvent.InfinitySyncTime && partition.leftIntervalMap.IsEmpty)
                partition.isLeftComplete = true;

            if (partition.nextRightTime == StreamEvent.InfinitySyncTime && partition.rightIntervalMap.IsEmpty)
                partition.isRightComplete = true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ReachTime(PartitionEntry partition)
        {
            while (partition.endPointHeap.TryGetNextInclusive(partition.currTime, out long endPointTime, out int index))
            {
                if (index >= 0)
                {
                    // Endpoint is left interval ending.
                    CreateOutputForEndInterval(
                        partition,
                        endPointTime,
                        partition.leftIntervalMap.Values[index].Start,
                        ref partition.leftIntervalMap.Values[index].Payload);
                    partition.leftIntervalMap.Remove(index);
                }
                else
                {
                    // Endpoint is right interval ending.
                    index = ~index;
                    CreateOutputForEndInterval(
                        partition,
                        endPointTime,
                        partition.rightIntervalMap.Values[index].Start,
                        ref partition.rightIntervalMap.Values[index].Payload);
                    partition.rightIntervalMap.Remove(index);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void CreateOutputForEndEdge(PartitionEntry partition, long currentTime, long start, ref TLeft payload)
        {
            // Create end edges for all joined right edges.
            var edges = partition.rightEdgeMap.Find(partition.hash);
            int index;
            while (edges.Next(out index))
            {
                long rightStart = partition.rightEdgeMap.Values[index].Start;
                AddToBatch(
                    currentTime,
                    start > rightStart ? start : rightStart,
                    partition,
                    ref payload,
                    ref partition.rightEdgeMap.Values[index].Payload);
            }

            // Create end edges for all joined right intervals.
            var intervals = partition.rightIntervalMap.Find(partition.hash);
            while (intervals.Next(out index))
            {
                long rightStart = partition.rightIntervalMap.Values[index].Start;
                AddToBatch(
                    currentTime,
                    start > rightStart ? start : rightStart,
                    partition,
                    ref payload,
                    ref partition.rightIntervalMap.Values[index].Payload);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void CreateOutputForEndEdge(PartitionEntry partition, long currentTime, long start, ref TRight payload)
        {
            // Create end edges for all joined left edges.
            var edges = partition.leftEdgeMap.Find(partition.hash);
            int index;
            while (edges.Next(out index))
            {
                long leftStart = partition.leftEdgeMap.Values[index].Start;
                AddToBatch(
                    currentTime,
                    start > leftStart ? start : leftStart,
                    partition,
                    ref partition.leftEdgeMap.Values[index].Payload,
                    ref payload);
            }

            // Create end edges for all joined left intervals.
            var intervals = partition.leftIntervalMap.Find(partition.hash);
            while (intervals.Next(out index))
            {
                long leftStart = partition.leftIntervalMap.Values[index].Start;
                AddToBatch(
                    currentTime,
                    start > leftStart ? start : leftStart,
                    partition,
                    ref partition.leftIntervalMap.Values[index].Payload,
                    ref payload);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void CreateOutputForStartEdge(PartitionEntry partition, long currentTime, ref TLeft payload)
        {
            // Create end edges for all joined right edges.
            var edges = partition.rightEdgeMap.Find(partition.hash);
            int index;
            while (edges.Next(out index))
            {
                AddToBatch(
                    currentTime,
                    StreamEvent.InfinitySyncTime,
                    partition,
                    ref payload,
                    ref partition.rightEdgeMap.Values[index].Payload);
            }

            // Create end edges for all joined right intervals.
            var intervals = partition.rightIntervalMap.Find(partition.hash);
            while (intervals.Next(out index))
            {
                AddToBatch(
                    currentTime,
                    StreamEvent.InfinitySyncTime,
                    partition,
                    ref payload,
                    ref partition.rightIntervalMap.Values[index].Payload);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void CreateOutputForStartEdge(PartitionEntry partition, long currentTime, ref TRight payload)
        {
            // Create end edges for all joined left edges.
            var edges = partition.leftEdgeMap.Find(partition.hash);
            int index;
            while (edges.Next(out index))
            {
                AddToBatch(
                    currentTime,
                    StreamEvent.InfinitySyncTime,
                    partition,
                    ref partition.leftEdgeMap.Values[index].Payload,
                    ref payload);
            }

            // Create end edges for all joined left intervals.
            var intervals = partition.leftIntervalMap.Find(partition.hash);
            while (intervals.Next(out index))
            {
                AddToBatch(
                    currentTime,
                    StreamEvent.InfinitySyncTime,
                    partition,
                    ref partition.leftIntervalMap.Values[index].Payload,
                    ref payload);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void CreateOutputForStartInterval(PartitionEntry partition, long currentTime, long end, ref TLeft payload)
        {
            // Create end edges for all joined right edges.
            var edges = partition.rightEdgeMap.Find(partition.hash);
            int index;
            while (edges.Next(out index))
            {
                AddToBatch(
                    currentTime,
                    StreamEvent.InfinitySyncTime,
                    partition,
                    ref payload,
                    ref partition.rightEdgeMap.Values[index].Payload);
            }

            // Create end edges for all joined right intervals.
            var intervals = partition.rightIntervalMap.Find(partition.hash);
            while (intervals.Next(out index))
            {
                long rightEnd = partition.rightIntervalMap.Values[index].End;
                AddToBatch(
                    currentTime,
                    end < rightEnd ? end : rightEnd,
                    partition,
                    ref payload,
                    ref partition.rightIntervalMap.Values[index].Payload);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void CreateOutputForStartInterval(PartitionEntry partition, long currentTime, long end, ref TRight payload)
        {
            // Create end edges for all joined left edges.
            var edges = partition.leftEdgeMap.Find(partition.hash);
            int index;
            while (edges.Next(out index))
            {
                AddToBatch(
                    currentTime,
                    StreamEvent.InfinitySyncTime,
                    partition,
                    ref partition.leftEdgeMap.Values[index].Payload,
                    ref payload);
            }

            // Create end edges for all joined left intervals.
            var intervals = partition.leftIntervalMap.Find(partition.hash);
            while (intervals.Next(out index))
            {
                long leftEnd = partition.leftIntervalMap.Values[index].End;
                AddToBatch(
                    currentTime,
                    end < leftEnd ? end : leftEnd,
                    partition,
                    ref partition.leftIntervalMap.Values[index].Payload,
                    ref payload);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void CreateOutputForEndInterval(PartitionEntry partition, long currentTime, long start, ref TLeft payload)
        {
            // Create end edges for all joined right edges.
            var edges = partition.rightEdgeMap.Find(partition.hash);
            while (edges.Next(out int index))
            {
                long rightStart = partition.rightEdgeMap.Values[index].Start;
                AddToBatch(
                    currentTime,
                    start > rightStart ? start : rightStart,
                    partition,
                    ref payload,
                    ref partition.rightEdgeMap.Values[index].Payload);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void CreateOutputForEndInterval(PartitionEntry partition, long currentTime, long start, ref TRight payload)
        {
            // Create end edges for all joined left edges.
            var edges = partition.leftEdgeMap.Find(partition.hash);
            while (edges.Next(out int index))
            {
                long leftStart = partition.leftEdgeMap.Values[index].Start;
                AddToBatch(
                    currentTime,
                    start > leftStart ? start : leftStart,
                    partition,
                    ref partition.leftEdgeMap.Values[index].Payload,
                    ref payload);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void AddLowWatermarkToBatch(long start)
        {
            if (start > this.lastCTI)
            {
                this.lastCTI = start;

                int index = this.output.Count++;
                this.output.vsync.col[index] = start;
                this.output.vother.col[index] = PartitionedStreamEvent.LowWatermarkOtherTime;
                this.output.key.col[index] = new PartitionKey<TPartitionKey>(default);
                this.output[index] = default;
                this.output.hash.col[index] = 0;
                this.output.bitvector.col[index >> 6] |= 1L << (index & 0x3f);

                if (this.output.Count == Config.DataBatchSize) FlushContents();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void AddToBatch(long start, long end, PartitionEntry partition, ref TLeft leftPayload, ref TRight rightPayload)
        {
            if (start < this.lastCTI)
            {
                throw new StreamProcessingOutOfOrderException("Outputting an event out of order!");
            }

            int index = this.output.Count++;
            this.output.vsync.col[index] = start;
            this.output.vother.col[index] = end;
            this.output.key.col[index] = new PartitionKey<TPartitionKey>(partition.key);
            this.output.hash.col[index] = partition.hash;

            if (end < 0)
                this.output.bitvector.col[index >> 6] |= 1L << (index & 0x3f);
            else
                this.output[index] = this.selector(leftPayload, rightPayload);
            if (this.output.Count == Config.DataBatchSize) FlushContents();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool AreSame(long start, ref TLeft payload, ref ActiveEdge<TLeft> active)
            => start == active.Start && this.leftComparerEquals(payload, active.Payload);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool AreSame(long start, ref TRight payload, ref ActiveEdge<TRight> active)
            => start == active.Start && this.rightComparerEquals(payload, active.Payload);

        protected override void FlushContents()
        {
            if (this.output.Count == 0) return;
            this.output.Seal();
            this.Observer.OnNext(this.output);
            this.pool.Get(out this.output);
            this.output.Allocate();
        }

        public override int CurrentlyBufferedOutputCount => this.output.Count;
        public override int CurrentlyBufferedLeftInputCount
        {
            get
            {
                int count = base.CurrentlyBufferedLeftInputCount;
                int key = FastDictionary2<TPartitionKey, Queue<LEntry>>.IteratorStart;
                while (this.leftQueue.Iterate(ref key)) count += this.leftQueue.entries[key].value.Count;
                int iter = FastDictionary<TPartitionKey, PartitionEntry>.IteratorStart;
                while (this.partitionData.Iterate(ref iter))
                {
                    var p = this.partitionData.entries[iter].value;
                    count += p.leftEdgeMap.Count + p.leftIntervalMap.Count;
                }
                return count;
            }
        }
        public override int CurrentlyBufferedRightInputCount
        {
            get
            {
                int count = base.CurrentlyBufferedRightInputCount;
                int key = FastDictionary2<TPartitionKey, Queue<REntry>>.IteratorStart;
                while (this.rightQueue.Iterate(ref key)) count += this.rightQueue.entries[key].value.Count;
                int iter = FastDictionary<TPartitionKey, PartitionEntry>.IteratorStart;
                while (this.partitionData.Iterate(ref iter))
                {
                    var p = this.partitionData.entries[iter].value;
                    count += p.rightEdgeMap.Count + p.rightIntervalMap.Count;
                }
                return count;
            }
        }
        public override int CurrentlyBufferedLeftKeyCount
        {
            get
            {
                var keys = new HashSet<TPartitionKey>();
                int key = FastDictionary2<TPartitionKey, PooledElasticCircularBuffer<LEntry>>.IteratorStart;
                while (this.leftQueue.Iterate(ref key)) if (this.leftQueue.entries[key].value.Count > 0) keys.Add(this.leftQueue.entries[key].key);
                int iter = FastDictionary<TPartitionKey, PartitionEntry>.IteratorStart;
                while (this.partitionData.Iterate(ref iter))
                {
                    var p = this.partitionData.entries[iter].value;
                    if (p.leftEdgeMap.Count > 0) keys.Add(this.partitionData.entries[iter].key);
                    if (p.leftIntervalMap.Count > 0) keys.Add(this.partitionData.entries[iter].key);
                }
                return keys.Count;
            }
        }
        public override int CurrentlyBufferedRightKeyCount
        {
            get
            {
                var keys = new HashSet<TPartitionKey>();
                int key = FastDictionary2<TPartitionKey, Queue<REntry>>.IteratorStart;
                while (this.rightQueue.Iterate(ref key)) if (this.rightQueue.entries[key].value.Count > 0) keys.Add(this.rightQueue.entries[key].key);
                int iter = FastDictionary<TPartitionKey, PartitionEntry>.IteratorStart;
                while (this.partitionData.Iterate(ref iter))
                {
                    var p = this.partitionData.entries[iter].value;
                    if (p.rightEdgeMap.Count > 0) keys.Add(this.partitionData.entries[iter].key);
                    if (p.rightIntervalMap.Count > 0) keys.Add(this.partitionData.entries[iter].key);
                }
                return keys.Count;
            }
        }

        [DataContract]
        private struct ActiveInterval<TPayload>
        {
            [DataMember]
            public long Start;
            [DataMember]
            public long End;
            [DataMember]
            public TPayload Payload;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Populate(long start, long end, ref TPayload payload)
            {
                this.Start = start;
                this.End = end;
                this.Payload = payload;
            }

            public override string ToString() => "[Start=" + this.Start + ", End=" + this.End + ", Payload='" + this.Payload + "']";
        }

        [DataContract]
        private struct ActiveEdge<TPayload>
        {
            [DataMember]
            public long Start;
            [DataMember]
            public TPayload Payload;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Populate(long start, ref TPayload payload)
            {
                this.Start = start;
                this.Payload = payload;
            }

            public override string ToString() => "[Start=" + this.Start + ", Payload='" + this.Payload + "']";
        }

        public override bool LeftInputHasState
        {
            get
            {
                int index = FastDictionary<TPartitionKey, HashSet<PartitionedStreamEvent<TPartitionKey, TLeft>>>.IteratorStart;
                while (this.leftQueue.Iterate(ref index)) if (this.leftQueue.entries[index].value.Count > 0) return true;
                return false;
            }
        }

        public override bool RightInputHasState
        {
            get
            {
                int index = FastDictionary<TPartitionKey, HashSet<PartitionedStreamEvent<TPartitionKey, TRight>>>.IteratorStart;
                while (this.rightQueue.Iterate(ref index)) if (this.rightQueue.entries[index].value.Count > 0) return true;
                return false;
            }
        }

        [DataContract]
        private sealed class LEntry
        {
            [DataMember]
            public long Sync;
            [DataMember]
            public long Other;
            [DataMember]
            public TLeft Payload;
        }

        [DataContract]
        private sealed class REntry
        {
            [DataMember]
            public long Sync;
            [DataMember]
            public long Other;
            [DataMember]
            public TRight Payload;
        }

        [DataContract]
        private sealed class PartitionEntry
        {
            [DataMember]
            public TPartitionKey key;
            [DataMember]
            public int hash;

            /// <summary>
            /// Stores left intervals starting at <see cref="currTime"/>.
            /// FastMap visibility means that <see cref="nextRightTime"/> is caught up to the left edge time and the
            /// left edge is processable.
            /// </summary>
            [DataMember]
            public FastMap<ActiveInterval<TLeft>> leftIntervalMap = new FastMap<ActiveInterval<TLeft>>();

            /// <summary>
            /// Stores left start edges at <see cref="currTime"/>
            /// FastMap visibility means that <see cref="nextRightTime"/> is caught up to the left edge time and the
            /// left edge is processable.
            /// </summary>
            [DataMember]
            public FastMap<ActiveEdge<TLeft>> leftEdgeMap = new FastMap<ActiveEdge<TLeft>>();

            /// <summary>
            /// Stores end edges for the current join at some point in the future, i.e. after <see cref="currTime"/>.
            /// These can originate from edge end events or interval events.
            /// </summary>
            [DataMember]
            public IEndPointOrderer endPointHeap;

            /// <summary>
            /// Stores right intervals starting at <see cref="currTime"/>.
            /// FastMap visibility means that <see cref="nextRightTime"/> is caught up to the right edge time and the
            /// right edge is processable.
            /// </summary>
            [DataMember]
            public FastMap<ActiveInterval<TRight>> rightIntervalMap = new FastMap<ActiveInterval<TRight>>();

            /// <summary>
            /// Stores right start edges at <see cref="currTime"/>
            /// FastMap visibility means that <see cref="nextRightTime"/> is caught up to the right edge time and the
            /// right edge is processable.
            /// </summary>
            [DataMember]
            public FastMap<ActiveEdge<TRight>> rightEdgeMap = new FastMap<ActiveEdge<TRight>>();

            [DataMember]
            public long nextLeftTime = long.MinValue;

            /// <summary>
            /// True if left has reached StreamEvent.InfinitySyncTime
            /// </summary>
            [DataMember]
            public bool isLeftComplete = false;

            [DataMember]
            public long nextRightTime = long.MinValue;

            /// <summary>
            /// True if right has reached StreamEvent.InfinitySyncTime
            /// </summary>
            [DataMember]
            public bool isRightComplete = false;

            [DataMember]
            public long currTime = long.MinValue;

            public bool IsClean() => this.leftIntervalMap.IsEmpty &&
                                     this.leftEdgeMap.IsEmpty &&
                                     this.endPointHeap.IsEmpty &&
                                     this.rightIntervalMap.IsEmpty &&
                                     this.rightEdgeMap.IsEmpty;
        }
    }
}

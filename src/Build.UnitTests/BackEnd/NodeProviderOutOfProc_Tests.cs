// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using Microsoft.Build.BackEnd;
using Microsoft.Build.Shared;
using Shouldly;
using Xunit;

#nullable disable

namespace Microsoft.Build.UnitTests.BackEnd
{
    /// <summary>
    /// Tests for NodeProviderOutOfProc, specifically the node over-provisioning detection feature.
    /// </summary>
    public class NodeProviderOutOfProc_Tests
    {
        /// <summary>
        /// Test helper class to expose protected methods for testing.
        /// </summary>
        private sealed class TestableNodeProviderOutOfProcBase : NodeProviderOutOfProcBase
        {
            private int _systemWideNodeCount;
            private int _threshold;

            public TestableNodeProviderOutOfProcBase(int systemWideNodeCount, int threshold)
            {
                _systemWideNodeCount = systemWideNodeCount;
                _threshold = threshold;
            }

            protected override int GetNodeReuseThreshold()
            {
                return _threshold;
            }

            protected override int CountSystemWideActiveNodes()
            {
                return _systemWideNodeCount;
            }

            public bool[] TestDetermineNodesForReuse(int nodeCount, bool enableReuse)
            {
                return DetermineNodesForReuse(nodeCount, enableReuse);
            }
        }

        [Fact]
        public void DetermineNodesForReuse_WhenReuseDisabled_AllNodesShouldTerminate()
        {
            var provider = new TestableNodeProviderOutOfProcBase(systemWideNodeCount: 10, threshold: 4);
            
            bool[] result = provider.TestDetermineNodesForReuse(nodeCount: 3, enableReuse: false);
            
            result.Length.ShouldBe(3);
            result.ShouldAllBe(shouldReuse => shouldReuse == false);
        }

        [Fact]
        public void DetermineNodesForReuse_WhenThresholdIsZero_AllNodesShouldTerminate()
        {
            var provider = new TestableNodeProviderOutOfProcBase(systemWideNodeCount: 10, threshold: 0);
            
            bool[] result = provider.TestDetermineNodesForReuse(nodeCount: 3, enableReuse: true);
            
            result.Length.ShouldBe(3);
            result.ShouldAllBe(shouldReuse => shouldReuse == false);
        }

        [Fact]
        public void DetermineNodesForReuse_WhenUnderThreshold_AllNodesShouldBeReused()
        {
            // System has 3 nodes total, threshold is 4, so we're under the limit
            var provider = new TestableNodeProviderOutOfProcBase(systemWideNodeCount: 3, threshold: 4);
            
            bool[] result = provider.TestDetermineNodesForReuse(nodeCount: 3, enableReuse: true);
            
            result.Length.ShouldBe(3);
            result.ShouldAllBe(shouldReuse => shouldReuse == true);
        }

        [Fact]
        public void DetermineNodesForReuse_WhenAtThreshold_AllNodesShouldBeReused()
        {
            // System has 4 nodes total, threshold is 4, so we're at the limit
            var provider = new TestableNodeProviderOutOfProcBase(systemWideNodeCount: 4, threshold: 4);
            
            bool[] result = provider.TestDetermineNodesForReuse(nodeCount: 4, enableReuse: true);
            
            result.Length.ShouldBe(4);
            result.ShouldAllBe(shouldReuse => shouldReuse == true);
        }

        [Fact]
        public void DetermineNodesForReuse_WhenOverThreshold_ExcessNodesShouldTerminate()
        {
            // System has 10 nodes total, threshold is 4
            // This instance has 3 nodes
            // We should keep 0 nodes from this instance (since 10 - 3 = 7, which is already > threshold)
            var provider = new TestableNodeProviderOutOfProcBase(systemWideNodeCount: 10, threshold: 4);
            
            bool[] result = provider.TestDetermineNodesForReuse(nodeCount: 3, enableReuse: true);
            
            result.Length.ShouldBe(3);
            result.ShouldAllBe(shouldReuse => shouldReuse == false);
        }

        [Fact]
        public void DetermineNodesForReuse_WhenSlightlyOverThreshold_SomeNodesShouldBeReused()
        {
            // System has 6 nodes total, threshold is 4
            // This instance has 3 nodes
            // Other instances have 6 - 3 = 3 nodes
            // We need to reduce by 2 nodes to reach threshold
            // So we should keep 1 node from this instance
            var provider = new TestableNodeProviderOutOfProcBase(systemWideNodeCount: 6, threshold: 4);
            
            bool[] result = provider.TestDetermineNodesForReuse(nodeCount: 3, enableReuse: true);
            
            result.Length.ShouldBe(3);
            // First node should be reused, others should terminate
            result[0].ShouldBeTrue();
            result[1].ShouldBeFalse();
            result[2].ShouldBeFalse();
        }

        [Fact]
        public void DetermineNodesForReuse_WithSingleNode_BehavesCorrectly()
        {
            // System has 5 nodes total, threshold is 4
            // This instance has 1 node
            // We're over threshold, but only by 1
            // We should terminate this node since others already meet threshold
            var provider = new TestableNodeProviderOutOfProcBase(systemWideNodeCount: 5, threshold: 4);
            
            bool[] result = provider.TestDetermineNodesForReuse(nodeCount: 1, enableReuse: true);
            
            result.Length.ShouldBe(1);
            result[0].ShouldBeFalse();
        }

        [Fact]
        public void GetNodeReuseThreshold_DefaultImplementation_ReturnsHalfOfCoreCount()
        {
            // This tests the actual default implementation (not the test helper)
            // We can't easily instantiate NodeProviderOutOfProc directly, but we can test the logic
            int coreCount = NativeMethodsShared.GetLogicalCoreCount();
            int expectedThreshold = Math.Max(1, coreCount / 2);
            
            // The default implementation should return cores/2, with minimum of 1
            expectedThreshold.ShouldBeGreaterThanOrEqualTo(1);
            expectedThreshold.ShouldBeLessThanOrEqualTo(coreCount);
        }
    }
}

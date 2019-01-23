﻿using System;
using System.Linq;
using System.Reactive.Subjects;
using System.Reactive.Linq;
using DynamicData;
using Xunit;
using System.Reactive;
using DynamicData.Binding;
using System.Reactive.Concurrency;

namespace Punchclock.Tests
{
    public class OperationQueueTests
    {
        [Fact]
        public void ItemsShouldBeDispatchedByPriority()
        {
            var subjects = Enumerable.Range(0, 5).Select(x => new AsyncSubject<int>()).ToArray();
            var priorities = new[] { 5, 5, 5, 10, 1, };
            var fixture = new OperationQueue(2);

            // The two at the front are solely to stop up the queue, they get subscribed 
            // to immediately.
            var outputs = subjects.Zip(priorities,
                (inp, pri) => {
                    fixture
                        .EnqueueObservableOperation(pri, () => inp)
                        .ToObservableChangeSet(scheduler: ImmediateScheduler.Instance)
                        .Bind(out var y).Subscribe();
                    return y;
                }).ToArray();

            // Alright, we've got the first two subjects taking up our two live 
            // slots, and 3,4,5 queued up. However, the order of completion should 
            // be "4,3,5" because of the priority.
            Assert.True(outputs.All(x => x.Count() == 0));

            subjects[0].OnNext(42); subjects[0].OnCompleted();
            Assert.Equal(new[] { 1, 0, 0, 0, 0, }, outputs.Select(x => x.Count));

            // 0 => completed, 1,3 => live, 2,4 => queued. Make sure 4 *doesn't* fire because 
            // the priority should invert it.
            subjects[4].OnNext(42); subjects[4].OnCompleted();
            Assert.Equal(new[] { 1, 0, 0, 0, 0, }, outputs.Select(x => x.Count));

            // At the end, 0,1 => completed, 3,2 => live, 4 is queued
            subjects[1].OnNext(42); subjects[1].OnCompleted();
            Assert.Equal(new[] { 1, 1, 0, 0, 0, }, outputs.Select(x => x.Count));

            // At the end, 0,1,2,4 => completed, 3 is live (remember, we completed
            // 4 early)
            subjects[2].OnNext(42); subjects[2].OnCompleted();
            Assert.Equal(new[] { 1, 1, 1, 0, 1, }, outputs.Select(x => x.Count));

            subjects[3].OnNext(42); subjects[3].OnCompleted();
            Assert.Equal(new[] { 1, 1, 1, 1, 1, }, outputs.Select(x => x.Count));
        }

        [Fact]
        public void KeyedItemsShouldBeSerialized()
        {
            var subj1 = new AsyncSubject<int>();
            var subj2 = new AsyncSubject<int>();

            var subscribeCount1 = 0;
            var input1Subj = new AsyncSubject<int>();
            var input1 = Observable.Defer(() => {
                subscribeCount1++;
                return input1Subj;
            });
            var subscribeCount2 = 0;
            var input2Subj = new AsyncSubject<int>();
            var input2 = Observable.Defer(() => {
                subscribeCount2++;
                return input2Subj;
            });

            var fixture = new OperationQueue(2);

            // Block up the queue
            foreach (var v in new[] { subj1, subj2, }) {
                fixture.EnqueueObservableOperation(5, () => v);
            }

            // subj1,2 are live, input1,2 are in queue
            fixture
                .EnqueueObservableOperation(5, "key", Observable.Never<Unit>(), () => input1)
                .ToObservableChangeSet(scheduler: ImmediateScheduler.Instance)
                .Bind(out var out1).Subscribe();
            fixture
                .EnqueueObservableOperation(5, "key", Observable.Never<Unit>(), () => input2)
                .ToObservableChangeSet(scheduler: ImmediateScheduler.Instance)
                .Bind(out var out2).Subscribe();

            Assert.Equal(0, subscribeCount1);
            Assert.Equal(0, subscribeCount2);

            // Dispatch both subj1 and subj2, we should end up with input1 live, 
            // but input2 in queue because of the key
            subj1.OnNext(42); subj1.OnCompleted();
            subj2.OnNext(42); subj2.OnCompleted();
            Assert.Equal(1, subscribeCount1);
            Assert.Equal(0, subscribeCount2);
            Assert.Equal(0, out1.Count);
            Assert.Equal(0, out2.Count);

            // Dispatch input1, input2 can now execute
            input1Subj.OnNext(42); input1Subj.OnCompleted();
            Assert.Equal(1, subscribeCount1);
            Assert.Equal(1, subscribeCount2);
            Assert.Equal(1, out1.Count);
            Assert.Equal(0, out2.Count);

            // Dispatch input2, everything is finished
            input2Subj.OnNext(42); input2Subj.OnCompleted();
            Assert.Equal(1, subscribeCount1);
            Assert.Equal(1, subscribeCount2);
            Assert.Equal(1, out1.Count);
            Assert.Equal(1, out2.Count);
        }

        [Fact]
        public void NonkeyedItemsShouldRunInParallel()
        {
            var unkeyed1Subj = new AsyncSubject<int>();
            var unkeyed1SubCount = 0;
            var unkeyed1 = Observable.Defer(() => {
                unkeyed1SubCount++;
                return unkeyed1Subj;
            });

            var unkeyed2Subj = new AsyncSubject<int>();
            var unkeyed2SubCount = 0;
            var unkeyed2 = Observable.Defer(() => {
                unkeyed2SubCount++;
                return unkeyed2Subj;
            });

            var fixture = new OperationQueue(2);
            Assert.Equal(0, unkeyed1SubCount);
            Assert.Equal(0, unkeyed2SubCount);

            fixture.EnqueueObservableOperation(5, () => unkeyed1);
            fixture.EnqueueObservableOperation(5, () => unkeyed2);
            Assert.Equal(1, unkeyed1SubCount);
            Assert.Equal(1, unkeyed2SubCount);
        }

        [Fact]
        public void ShutdownShouldSignalOnceEverythingCompletes()
        {
            var subjects = Enumerable.Range(0, 5).Select(x => new AsyncSubject<int>()).ToArray();
            var priorities = new[] {5,5,5,10,1,};
            var fixture = new OperationQueue(2);

            // The two at the front are solely to stop up the queue, they get subscribed 
            // to immediately.
            var outputs = subjects.Zip(priorities,
                (inp, pri) => {
                    fixture
                        .EnqueueObservableOperation(pri, () => inp)
                        .ToObservableChangeSet(scheduler: ImmediateScheduler.Instance)
                        .Bind(out var output).Subscribe();
                    return output;
                }).ToArray();

            fixture
                .ShutdownQueue()
                .ToObservableChangeSet(scheduler: ImmediateScheduler.Instance)
                .Bind(out var shutdown).Subscribe();

            Assert.True(outputs.All(x => x.Count == 0));
            Assert.Equal(0, shutdown.Count);

            for (int i = 0; i < 4; i++) { subjects[i].OnNext(42); subjects[i].OnCompleted(); } 
            Assert.Equal(0, shutdown.Count);

            // Complete the last one, that should signal that we're shut down
            subjects[4].OnNext(42); subjects[4].OnCompleted();
            Assert.True(outputs.All(x => x.Count == 1));
            Assert.Equal(1, shutdown.Count);
        }

        [Fact]
        public void PausingTheQueueShouldHoldItemsUntilUnpaused()
        {
            var item = Observable.Return(42);

            var fixture = new OperationQueue(2);
            new[] {
                fixture.EnqueueObservableOperation(4, () => item),
                fixture.EnqueueObservableOperation(4, () => item),
            }.Merge()
             .ToObservableChangeSet(scheduler: ImmediateScheduler.Instance)
             .Bind(out var prePauseOutput).Subscribe();

            Assert.Equal(2, prePauseOutput.Count);

            var unpause1 = fixture.PauseQueue();

            // The queue is halted, but we should still eventually process these
            // once it's no longer halted
            new[] {
                fixture.EnqueueObservableOperation(4, () => item),
                fixture.EnqueueObservableOperation(4, () => item),
            }.Merge()
             .ToObservableChangeSet(scheduler: ImmediateScheduler.Instance)
             .Bind(out var pauseOutput).Subscribe();

            Assert.Equal(0, pauseOutput.Count);

            var unpause2 = fixture.PauseQueue();
            Assert.Equal(0, pauseOutput.Count);

            unpause1.Dispose();
            Assert.Equal(0, pauseOutput.Count);

            unpause2.Dispose();
            Assert.Equal(2, pauseOutput.Count);
        }

        [Fact]
        public void CancellingItemsShouldNotResultInThemBeingReturned()
        {
            var subj1 = new AsyncSubject<int>();
            var subj2 = new AsyncSubject<int>();

            var fixture = new OperationQueue(2);

            // Block up the queue
            foreach (var v in new[] { subj1, subj2, }) {
                fixture.EnqueueObservableOperation(5, () => v);
            }

            var cancel1 = new Subject<Unit>();
            var item1 = new AsyncSubject<int>();
            new[] {
                fixture.EnqueueObservableOperation(5, "foo", cancel1, () => item1),
                fixture.EnqueueObservableOperation(5, "baz", () => Observable.Return(42)),
            }.Merge()
             .ToObservableChangeSet(scheduler: ImmediateScheduler.Instance)
             .Bind(out var output).Subscribe();


            // Still blocked by subj1,2
            Assert.Equal(0, output.Count);

            // Still blocked by subj1,2, only baz is in queue
            cancel1.OnNext(Unit.Default); cancel1.OnCompleted();
            Assert.Equal(0, output.Count);

            // foo was cancelled, baz is still good
            subj1.OnNext(42); subj1.OnCompleted();
            Assert.Equal(1, output.Count);

            // don't care that cancelled item finished
            item1.OnNext(42); item1.OnCompleted();
            Assert.Equal(1, output.Count);

            // still shouldn't see anything
            subj2.OnNext(42); subj2.OnCompleted();
            Assert.Equal(1, output.Count);
        }

        [Fact]
        public void CancellingItemsShouldntEvenBeEvaluated()
        {
            var subj1 = new AsyncSubject<int>();
            var subj2 = new AsyncSubject<int>();

            var fixture = new OperationQueue(2);

            // Block up the queue
            foreach (var v in new[] { subj1, subj2, }) {
                fixture.EnqueueObservableOperation(5, () => v);
            }

            var cancel1 = new Subject<Unit>();
            bool wasCalled = false;
            var item1 = new AsyncSubject<int>();

            fixture.EnqueueObservableOperation(5, "foo", cancel1, () => {
                wasCalled = true;
                return item1;
            }).ToObservableChangeSet(scheduler: ImmediateScheduler.Instance)
              .Bind(out var output).Subscribe();

            // Still blocked by subj1,2
            Assert.Equal(0, output.Count);
            Assert.False(wasCalled);

            // Still blocked by subj1,2 - however, we've cancelled foo before
            // it even had a chance to run - if that's the case, we shouldn't 
            // even call the evaluation func
            cancel1.OnNext(Unit.Default); cancel1.OnCompleted();
            Assert.Equal(0, output.Count);
            Assert.False(wasCalled);

            // Unblock subj1,2, we still shouldn't see wasCalled = true
            subj1.OnNext(42); subj1.OnCompleted();
            Assert.Equal(0, output.Count);
            Assert.False(wasCalled);

            subj2.OnNext(42); subj2.OnCompleted();
            Assert.Equal(0, output.Count);
            Assert.False(wasCalled);
        }

        [Fact]
        public void QueueShouldRespectMaximumConcurrent()
        {
            var unkeyed1Subj = new AsyncSubject<int>();
            var unkeyed1SubCount = 0;
            var unkeyed1 = Observable.Defer(() => {
                unkeyed1SubCount++;
                return unkeyed1Subj;
            });

            var unkeyed2Subj = new AsyncSubject<int>();
            var unkeyed2SubCount = 0;
            var unkeyed2 = Observable.Defer(() => {
                unkeyed2SubCount++;
                return unkeyed2Subj;
            });

            var unkeyed3Subj = new AsyncSubject<int>();
            var unkeyed3SubCount = 0;
            var unkeyed3 = Observable.Defer(() => {
                unkeyed3SubCount++;
                return unkeyed3Subj;
            });

            var fixture = new OperationQueue(2);
            Assert.Equal(0, unkeyed1SubCount);
            Assert.Equal(0, unkeyed2SubCount);
            Assert.Equal(0, unkeyed3SubCount);

            fixture.EnqueueObservableOperation(5, () => unkeyed1);
            fixture.EnqueueObservableOperation(5, () => unkeyed2);
            fixture.EnqueueObservableOperation(5, () => unkeyed3);

            Assert.Equal(1, unkeyed1SubCount);
            Assert.Equal(1, unkeyed2SubCount);
            Assert.Equal(0, unkeyed3SubCount);
        }

        [Fact]
        public void ShouldBeAbleToIncreaseTheMaximunConcurrentValueOfAnExistingQueue()
        {
            var unkeyed1Subj = new AsyncSubject<int>();
            var unkeyed1SubCount = 0;
            var unkeyed1 = Observable.Defer(() => {
                unkeyed1SubCount++;
                return unkeyed1Subj;
            });

            var unkeyed2Subj = new AsyncSubject<int>();
            var unkeyed2SubCount = 0;
            var unkeyed2 = Observable.Defer(() => {
                unkeyed2SubCount++;
                return unkeyed2Subj;
            });

            var unkeyed3Subj = new AsyncSubject<int>();
            var unkeyed3SubCount = 0;
            var unkeyed3 = Observable.Defer(() => {
                unkeyed3SubCount++;
                return unkeyed3Subj;
            });

            var unkeyed4Subj = new AsyncSubject<int>();
            var unkeyed4SubCount = 0;
            var unkeyed4 = Observable.Defer(() => {
                unkeyed4SubCount++;
                return unkeyed4Subj;
            });

            var fixture = new OperationQueue(2);
            Assert.Equal(0, unkeyed1SubCount);
            Assert.Equal(0, unkeyed2SubCount);
            Assert.Equal(0, unkeyed3SubCount);
            Assert.Equal(0, unkeyed4SubCount);

            fixture.EnqueueObservableOperation(5, () => unkeyed1);
            fixture.EnqueueObservableOperation(5, () => unkeyed2);
            fixture.EnqueueObservableOperation(5, () => unkeyed3);
            fixture.EnqueueObservableOperation(5, () => unkeyed4);
            
            Assert.Equal(1, unkeyed1SubCount);
            Assert.Equal(1, unkeyed2SubCount);
            Assert.Equal(0, unkeyed3SubCount);
            Assert.Equal(0, unkeyed4SubCount);
            
            fixture.SetMaximumConcurrent(3);
            
            Assert.Equal(1, unkeyed1SubCount);
            Assert.Equal(1, unkeyed2SubCount);
            Assert.Equal(1, unkeyed3SubCount);
            Assert.Equal(0, unkeyed4SubCount);
        }

        [Fact]
        public void ShouldBeAbleToDecreaseTheMaximunConcurrentValueOfAnExistingQueue()
        {
            var subjects = Enumerable.Range(0, 6).Select(x => new AsyncSubject<int>()).ToArray();
            var fixture = new OperationQueue(3);

            // The three at the front are solely to stop up the queue, they get subscribed 
            // to immediately.
            var outputs = subjects
                .Select(inp => {
                    fixture
                        .EnqueueObservableOperation(5, () => inp)
                        .ToObservableChangeSet(scheduler: ImmediateScheduler.Instance)
                        .Bind(out var output).Subscribe();
                    return output;
                }).ToArray();

            Assert.True(
                new[] { true, true, true, false, false, false, }.Zip(subjects,
                (expected, subj) => new { expected, actual = subj.HasObservers, })
                .All(x => x.expected == x.actual));

            fixture.SetMaximumConcurrent(2);

            // Complete the first one, the last three subjects should still have
            // no observers because we reduced maximum concurrent
            subjects[0].OnNext(42); subjects[0].OnCompleted();

            Assert.True(
                new[] { false, true, true, false, false, false, }.Zip(subjects,
                (expected, subj) => new { expected, actual = subj.HasObservers, })
                .All(x => x.expected == x.actual));

            // Complete subj[1], now 2,3 are live
            subjects[1].OnNext(42); subjects[1].OnCompleted();

            Assert.True(
                new[] { false, false, true, true, false, false, }.Zip(subjects,
                (expected, subj) => new { expected, actual = subj.HasObservers, })
                .All(x => x.expected == x.actual));
        }
    }
}

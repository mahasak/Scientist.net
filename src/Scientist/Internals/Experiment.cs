﻿using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace GitHub.Internals
{
    /// <summary>
    /// An instance of an experiment. This actually runs the control and the candidate and measures the result.
    /// </summary>
    /// <typeparam name="T">The return type of the experiment</typeparam>
    internal class ExperimentInstance<T>
    {
        static Random _random = new Random(DateTimeOffset.UtcNow.Millisecond);

        readonly Func<Task<T>> _control;
        readonly Func<Task<T>> _candidate;
        readonly Func<T, T, bool> _resultComparer;
        readonly string _name;

        public ExperimentInstance(string name, Func<T> control, Func<T> candidate)
        {
            _name = name;
            _control = () => Task.FromResult(control());
            _candidate = () => Task.FromResult(candidate());
        }

        public ExperimentInstance(string name, Func<Task<T>> control, Func<Task<T>> candidate)
        {
            _name = name;
            _control = control;
            _candidate = candidate;
        }

        public ExperimentInstance(string name, Func<Task<T>> control, Func<Task<T>> candidate, Func<T, T, bool> resultComparer)
        {
            _name = name;
            _control = control;
            _candidate = candidate;
            _resultComparer = resultComparer;
        }

        public async Task<T> Run()
        {
            // Randomize ordering...
            var runControlFirst = _random.Next(0, 2) == 0;
            ExperimentResult controlResult;
            ExperimentResult candidateResult;

            if (runControlFirst)
            {
                controlResult = await Run(_control);
                candidateResult = await Run(_candidate);
            }
            else
            {
                candidateResult = await Run(_candidate);
                controlResult = await Run(_control);
            }

            // TODO: We need to compare that thrown exceptions are equivalent too https://github.com/github/scientist/blob/master/lib/scientist/observation.rb#L76
            // TODO: We're going to have to be a bit more sophisticated about this.
            bool success = CompareResults(controlResult, candidateResult);

            var observation = new Observation(_name, success, controlResult.Duration, candidateResult.Duration);

            // TODO: Make this Fire and forget so we don't have to wait for this
            // to complete before we return a result
            await Scientist.ObservationPublisher.Publish(observation);

            if (controlResult.ThrownException != null) throw controlResult.ThrownException;
            return controlResult.Result;
        }

        private bool CompareResults(ExperimentResult controlResult, ExperimentResult candidateResult)
        {
            // Check nulls before calling passed in comparer
            if (controlResult.Result == null && candidateResult.Result == null)
            {
                return true;
            }

            if ((controlResult.Result == null && candidateResult.Result != null)
                || (controlResult.Result != null && candidateResult.Result == null))
            {
                return false;
            }

            if (_resultComparer != null)
            {
                return _resultComparer(controlResult.Result, candidateResult.Result);                
            }

            var equatableResult = controlResult.Result as IEquatable<T>;
            if (equatableResult != null)
            {
                return equatableResult.Equals(candidateResult.Result);
            }

            return controlResult.Result != null && controlResult.Result.Equals(candidateResult.Result);          
        }

        static async Task<ExperimentResult> Run(Func<Task<T>> experimentCase)
        {
            var sw = new Stopwatch();
            sw.Start();
            try
            {
                var result = await experimentCase();
                sw.Stop();

                return new ExperimentResult(result, new TimeSpan(sw.ElapsedTicks));
            }
            catch (Exception e)
            {
                sw.Stop();
                return new ExperimentResult(e, new TimeSpan(sw.ElapsedTicks));
            }
        }

        class ExperimentResult
        {
            public ExperimentResult(T result, TimeSpan duration)
            {
                Result = result;
                Duration = duration;
            }

            public ExperimentResult(Exception exception, TimeSpan duration)
            {
                ThrownException = exception;
                Duration = duration;
            }

            public T Result { get; }

            public Exception ThrownException { get; }

            public TimeSpan Duration { get; }
        }
    }
}
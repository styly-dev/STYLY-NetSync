// PollWait.cs - Coroutine polling helper for PlayMode tests.
// Yields frames until a predicate holds or a deadline passes, so tests never
// rely on fixed WaitForSeconds delays.
using System;
using System.Collections;
using UnityEngine;

namespace Styly.NetSync.Tests
{
    internal static class PollWait
    {
        /// <summary>
        /// Yield frames until <paramref name="condition"/> returns true or
        /// <paramref name="timeoutSeconds"/> elapses. Fails the test on timeout.
        /// </summary>
        public static IEnumerator Until(Func<bool> condition, float timeoutSeconds, string message)
        {
            var deadline = Time.realtimeSinceStartup + timeoutSeconds;
            while (!condition())
            {
                if (Time.realtimeSinceStartup > deadline)
                {
                    NUnit.Framework.Assert.Fail($"Timed out after {timeoutSeconds}s waiting for: {message}");
                }
                yield return null;
            }
        }
    }
}

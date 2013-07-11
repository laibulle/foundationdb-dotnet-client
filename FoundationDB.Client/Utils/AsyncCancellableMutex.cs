﻿#region BSD Licence
/* Copyright (c) 2013, Doxense SARL
All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are met:
	* Redistributions of source code must retain the above copyright
	  notice, this list of conditions and the following disclaimer.
	* Redistributions in binary form must reproduce the above copyright
	  notice, this list of conditions and the following disclaimer in the
	  documentation and/or other materials provided with the distribution.
	* Neither the name of Doxense nor the
	  names of its contributors may be used to endorse or promote products
	  derived from this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL <COPYRIGHT HOLDER> BE LIABLE FOR ANY
DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */
#endregion

namespace FoundationDB.Client.Utils
{
	using System;
	using System.Collections.Concurrent;
	using System.Collections.Generic;
	using System.Runtime.ExceptionServices;
	using System.Threading;
	using System.Threading.Tasks;

	/// <summary>Implements a mutex that supports cancellation</summary>
	internal class AsyncCancellableMutex : TaskCompletionSource<object>
	{
		// The consumer just needs to await the Task and will be woken up if someone calls Set(..) / Abort() on the mutex OR if the CancellationToken provided in the ctor is signaled.
		// Optionally, Set() and Abort() can specify if the consumer will be woken up from the ThreadPool (asnyc = true) or probably inline (async = false)

		private static Action<object> s_cancellationCallback = new Action<object>(CancellationHandler);

		private const int CTR_NONE = 0;
		private const int CTR_REGISTERED = 1;
		private const int CTR_CANCELLED_OR_DISPOSED = 2;

		private int m_state;
		private CancellationToken m_ct;
		private CancellationTokenRegistration m_ctr;

		/// <summary>Handler called if the CancellationToken linked to a waiter is signaled</summary>
		/// <param name="state"></param>
		private static void CancellationHandler(object state)
		{
			// the state contains the weak reference on the waiter, that we need to unwrap...

			var weakRef = (WeakReference<AsyncCancellableMutex>)state;
			AsyncCancellableMutex waiter;
			if (weakRef.TryGetTarget(out waiter) && waiter != null)
			{ // still alive...
				waiter.Abort(async: true);
			}
		}

		public AsyncCancellableMutex(CancellationToken ct)
		{
			if (ct.CanBeCanceled)
			{
				m_ct = ct;
				try
				{
					m_state = CTR_REGISTERED;
					m_ctr = ct.Register(s_cancellationCallback, new WeakReference<AsyncCancellableMutex>(this), useSynchronizationContext: false);
				}
				catch
				{
					GC.SuppressFinalize(this);
					throw;
				}
			}
		}

		public void Set(bool async = false)
		{
			// we don't really care if the cancellation token has already fired (or is firing at the same time),
			// because TrySetResult(..) and TrySetCancelled(..) will fight it out by themselves
			// we just need to dispose the registration if it hasn't already been done
			if (Interlocked.CompareExchange(ref m_state, CTR_CANCELLED_OR_DISPOSED, CTR_REGISTERED) == CTR_REGISTERED)
			{
				m_ctr.Dispose();
			}

			if (async)
			{
				ThreadPool.QueueUserWorkItem((state) => { ((AsyncCancellableMutex)state).TrySetResult(null); }, this);
			}
			else
			{
				this.TrySetResult(null);
			}
		}

		public void Abort(bool async = false)
		{
			// we don't really care if the cancellation token has already fired (or is firing at the same time), because TrySetCancelled(..) will be called either way.
			// we just need to dispose the registration if it hasn't already been done
			if (Interlocked.CompareExchange(ref m_state, CTR_CANCELLED_OR_DISPOSED, CTR_REGISTERED) == CTR_REGISTERED)
			{
				m_ctr.Dispose();
			}

			if (async)
			{
				ThreadPool.QueueUserWorkItem((state) => { ((AsyncCancellableMutex)state).TrySetCanceled(); }, this);
			}
			else
			{
				this.TrySetCanceled();
			}
		}

	}

}

﻿#region BSD License
/* Copyright (c) 2013-2018, Doxense SAS
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

namespace FoundationDB.Linq.Providers
{
	using System;
	using System.Collections.Generic;
	using System.Runtime.CompilerServices;
	using System.Threading;
	using System.Threading.Tasks;
	using Doxense.Diagnostics.Contracts;
	using Doxense.Linq;
	using FoundationDB.Client;
	using FoundationDB.Linq.Expressions;
	using JetBrains.Annotations;

	/// <summary>Base class for all Async LINQ queries</summary>
	/// <typeparam name="T">Type of the items returned by this query. Single queries return a single <typeparamref name="T"/> while Sequence queries will return a <see cref="List{T}"/></typeparam>
	/// <remarks>The type <typeparamref name="T"/> will be int for queries that eint </remarks>
	public abstract class FdbAsyncQuery<T> : IFdbAsyncQueryable, IFdbAsyncQueryProvider
	{

		/// <summary>Async LINQ query that will execute under a retry loop on a specific Database instance</summary>
		protected FdbAsyncQuery([NotNull] IFdbDatabase db, FdbQueryExpression expression = null)
		{
			this.Database = db;
			this.Expression = expression;
		}

		/// <summary>Async LINQ query that will execute on a specific Transaction instance</summary>
		protected FdbAsyncQuery([NotNull] IFdbReadOnlyTransaction trans, FdbQueryExpression expression = null)
		{
			this.Transaction = trans;
			this.Expression = expression;
		}

		/// <summary>Query expression</summary>
		public FdbQueryExpression Expression { get; }

		/// <summary>Database used by the query (or null)</summary>
		public IFdbDatabase Database { [CanBeNull] get; }

		/// <summary>Transaction used by the query (or null)</summary>
		public IFdbReadOnlyTransaction Transaction { [CanBeNull] get; }

		/// <summary>Type of the elements returned by the query</summary>
		public virtual Type Type => this.Expression.Type;

		IFdbAsyncQueryProvider IFdbAsyncQueryable.Provider => this;

		/// <summary>Create a new query from a query expression</summary>
		public virtual IFdbAsyncQueryable CreateQuery(FdbQueryExpression expression)
		{
			// source queries are usually only intended to produce some sort of result
			throw new NotSupportedException();
		}

		/// <summary>Create a new typed query from a query expression</summary>
		public virtual IFdbAsyncQueryable<R> CreateQuery<R>([NotNull] FdbQueryExpression<R> expression)
		{
			if (expression == null) throw new ArgumentNullException(nameof(expression));

			if (this.Transaction != null)
				return new FdbAsyncSingleQuery<R>(this.Transaction, expression);
			else
				return new FdbAsyncSingleQuery<R>(this.Database, expression);
		}

		/// <summary>Create a new sequence query from a sequence expression</summary>
		public virtual IFdbAsyncSequenceQueryable<R> CreateSequenceQuery<R>([NotNull] FdbQuerySequenceExpression<R> expression)
		{
			if (expression == null) throw new ArgumentNullException(nameof(expression));

			if (this.Transaction != null)
				return new FdbAsyncSequenceQuery<R>(this.Transaction, expression);
			else
				return new FdbAsyncSequenceQuery<R>(this.Database, expression);
		}

		/// <summary>Execute the query and return the result asynchronously</summary>
		/// <typeparam name="R">Type of the expected result. Can be a <typeparamref name="T"/> for singleton queries or a <see cref="List{T}"/> for sequence queries</typeparam>
		public async Task<R> ExecuteAsync<R>([NotNull] FdbQueryExpression expression, CancellationToken ct)
		{
			if (expression == null) throw new ArgumentNullException(nameof(ct));
			ct.ThrowIfCancellationRequested();

			var result = await ExecuteInternal(expression, typeof(R), ct).ConfigureAwait(false);
			return (R)result;
		}

		/// <summary>Execute the query and return the result in the expected type</summary>
		protected virtual Task<object> ExecuteInternal([NotNull] FdbQueryExpression expression, Type resultType, CancellationToken ct)
		{
			switch(expression.Shape)
			{
				case FdbQueryShape.Single:
				{
					if (!expression.Type.IsAssignableFrom(resultType)) throw new InvalidOperationException(String.Format("Return type {0} does not match the sequence type {1}", resultType.Name, expression.Type.Name));
					return ExecuteSingleInternal(expression, resultType, ct);
				}

				case FdbQueryShape.Sequence:
					return ExecuteSequenceInternal(expression, resultType, ct);

				case FdbQueryShape.Void:
					return Task.FromResult(default(object));

				default:
					throw new InvalidOperationException("Invalid sequence shape");
			}
		}

		#region Single...

		[NotNull]
		private Func<IFdbReadOnlyTransaction, CancellationToken, Task<T>> CompileSingle([NotNull] FdbQueryExpression expression)
		{
			//TODO: caching !

			var expr = ((FdbQueryExpression<T>)expression).CompileSingle();
			//Console.WriteLine("Compiled single as:");
			//Console.WriteLine("> " + expr.GetDebugView().Replace("\r\n", "\r\n> "));
			return expr.Compile();
		}

		/// <summary>Execute the query and return a single element in the expected type</summary>
		protected virtual async Task<object> ExecuteSingleInternal([NotNull] FdbQueryExpression expression, Type resultType, CancellationToken ct)
		{
			var generator = CompileSingle(expression);

			var trans = this.Transaction;
			bool owned = false;
			try
			{
				if (trans == null)
				{
					owned = true;
					trans = this.Database.BeginTransaction(ct);
				}

				T result = await generator(trans, ct).ConfigureAwait(false);

				return result;

			}
			finally
			{
				if (owned && trans != null) trans.Dispose();
			}

		}

		#endregion

		#region Sequence...

		[NotNull]
		private Func<IFdbReadOnlyTransaction, IAsyncEnumerable<T>> CompileSequence([NotNull] FdbQueryExpression expression)
		{
#if false
			//TODO: caching !
			Console.WriteLine("Source expression:");
			Console.WriteLine("> " + expression.GetDebugView().Replace("\r\n", "\r\n> "));
#endif

			var expr = ((FdbQuerySequenceExpression<T>) expression).CompileSequence();
#if false
			Console.WriteLine("Compiled sequence as:");
			Console.WriteLine("> " + expr.GetDebugView().Replace("\r\n", "\r\n> "));
#endif
			return expr.Compile();
		}

		[NotNull]
		internal static IAsyncEnumerator<T> GetEnumerator([NotNull] FdbAsyncSequenceQuery<T> sequence, AsyncIterationHint mode)
		{
			var generator = sequence.CompileSequence(sequence.Expression);

			if (sequence.Transaction != null)
			{
				var source = generator(sequence.Transaction);
				Contract.Assert(source != null);
				return source is IConfigurableAsyncEnumerable<T> configurable ? configurable.GetAsyncEnumerator(sequence.Transaction.Cancellation, mode) : source.GetAsyncEnumerator();
			}

			//BUGBUG: how do we get a CancellationToken without a transaction?
			var ct = CancellationToken.None;

			IFdbTransaction trans = null;
			IAsyncEnumerator<T> iterator = null;
			bool success = true;
			try
			{
				trans = sequence.Database.BeginTransaction(ct);
				var source = generator(trans);
				Contract.Assert(source != null);
				iterator = source is IConfigurableAsyncEnumerable<T> configurable ? configurable.GetAsyncEnumerator(ct, mode) : source.GetAsyncEnumerator();

				return new TransactionIterator(trans, iterator);
			}
			catch (Exception)
			{
				success = false;
				throw;
			}
			finally
			{
				if (!success)
				{
					if (iterator != null) iterator.DisposeAsync().GetAwaiter().GetResult(); //HACKHACK: how do we await here?
					trans?.Dispose();
				}
			}
		}

		private sealed class TransactionIterator : IAsyncEnumerator<T>
		{
			private readonly IAsyncEnumerator<T> m_iterator;
			private readonly IFdbTransaction m_transaction;

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public TransactionIterator(IFdbTransaction transaction, IAsyncEnumerator<T> iterator)
			{
				m_transaction = transaction;
				m_iterator = iterator;
			}

			[MethodImpl(MethodImplOptions.AggressiveInlining)]
			public ValueTask<bool> MoveNextAsync()
			{
				return m_iterator.MoveNextAsync();
			}

			public T Current => m_iterator.Current;

			public async ValueTask DisposeAsync()
			{
				try
				{
					await m_iterator.DisposeAsync();
				}
				finally
				{
					m_transaction.Dispose();
				}
			}
		}

		/// <summary>Execute the query and return a list of elements in the expected type</summary>
		protected virtual async Task<object> ExecuteSequenceInternal(FdbQueryExpression expression, Type resultType, CancellationToken ct)
		{
			var generator = CompileSequence(expression);

			var trans = this.Transaction;
			bool owned = false;
			try
			{
				if (trans == null)
				{
					owned = true;
					trans = this.Database.BeginTransaction(ct);
				}

				var enumerable = generator(trans);

				object result;

				if (typeof(T[]).IsAssignableFrom(resultType))
				{
					result = await enumerable.ToArrayAsync(ct).ConfigureAwait(false);
				}
				else if (typeof(IEnumerable<T>).IsAssignableFrom(resultType))
				{
					result = await enumerable.ToListAsync(ct).ConfigureAwait(false);
				}
				else
				{
					throw new InvalidOperationException($"Sequence result type {resultType.Name} is not supported");
				}

				return result;
			}
			finally
			{
				if (owned) trans?.Dispose();
			}
		}

		#endregion

	}

}

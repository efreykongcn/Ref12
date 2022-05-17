// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;

namespace SLaks.Ref12.MetadataAsSource
{
	internal interface IBidirectionalMap<TKey, TValue>
	{
		bool IsEmpty { get; }

		bool TryGetValue(TKey key, out TValue value);
		bool TryGetKey(TValue value, out TKey key);

		TValue GetValueOrDefault(TKey key);
		TKey GetKeyOrDefault(TValue value);

		bool ContainsKey(TKey key);
		bool ContainsValue(TValue value);

		IBidirectionalMap<TKey, TValue> RemoveKey(TKey key);
		IBidirectionalMap<TKey, TValue> RemoveValue(TValue value);

		IBidirectionalMap<TKey, TValue> Add(TKey key, TValue value);

		IEnumerable<TKey> Keys { get; }
		IEnumerable<TValue> Values { get; }
	}

	internal class BidirectionalMap<TKey, TValue> : IBidirectionalMap<TKey, TValue>
	{
		public static readonly IBidirectionalMap<TKey, TValue> Empty =
			new BidirectionalMap<TKey, TValue>(ImmutableDictionary.Create<TKey, TValue>(), ImmutableDictionary.Create<TValue, TKey>());

		private readonly ImmutableDictionary<TKey, TValue> _forwardMap;
		private readonly ImmutableDictionary<TValue, TKey> _backwardMap;

		public BidirectionalMap(IEnumerable<KeyValuePair<TKey, TValue>> pairs)
		{
			_forwardMap = ImmutableDictionary.CreateRange<TKey, TValue>(pairs);
			_backwardMap = ImmutableDictionary.CreateRange<TValue, TKey>(pairs.Select(p => KeyValuePairUtil.Create(p.Value, p.Key)));
		}

		private BidirectionalMap(ImmutableDictionary<TKey, TValue> forwardMap, ImmutableDictionary<TValue, TKey> backwardMap)
		{
			_forwardMap = forwardMap;
			_backwardMap = backwardMap;
		}

		public bool TryGetValue(TKey key, out TValue value)
			=> _forwardMap.TryGetValue(key, out value);

		public bool TryGetKey(TValue value, out TKey key)
			=> _backwardMap.TryGetValue(value, out key);

		public bool ContainsKey(TKey key)
			=> _forwardMap.ContainsKey(key);

		public bool ContainsValue(TValue value)
			=> _backwardMap.ContainsKey(value);

		public IBidirectionalMap<TKey, TValue> RemoveKey(TKey key)
		{
			if (!_forwardMap.TryGetValue(key, out var value))
			{
				return this;
			}

			return new BidirectionalMap<TKey, TValue>(
				_forwardMap.Remove(key),
				_backwardMap.Remove(value));
		}

		public IBidirectionalMap<TKey, TValue> RemoveValue(TValue value)
		{
			if (!_backwardMap.TryGetValue(value, out var key))
			{
				return this;
			}

			return new BidirectionalMap<TKey, TValue>(
				_forwardMap.Remove(key),
				_backwardMap.Remove(value));
		}

		public IBidirectionalMap<TKey, TValue> Add(TKey key, TValue value)
		{
			return new BidirectionalMap<TKey, TValue>(
				_forwardMap.Add(key, value),
				_backwardMap.Add(value, key));
		}

		public IEnumerable<TKey> Keys => _forwardMap.Keys;

		public IEnumerable<TValue> Values => _backwardMap.Keys;

		public bool IsEmpty
		{
			get
			{
				return _backwardMap.Count == 0;
			}
		}

		public int Count
		{
			get
			{
				Debug.Assert(_forwardMap.Count == _backwardMap.Count);
				return _backwardMap.Count;
			}
		}

		public TValue GetValueOrDefault(TKey key)
		{
			if (TryGetValue(key, out var result))
			{
				return result;
			}

			return default;
		}

		public TKey GetKeyOrDefault(TValue value)
		{
			if (TryGetKey(value, out var result))
			{
				return result;
			}

			return default;
		}
	}

	internal static class KeyValuePairUtil
	{
		public static KeyValuePair<K, V> Create<K, V>(K key, V value)
		{
			return new KeyValuePair<K, V>(key, value);
		}

		public static void Deconstruct<TKey, TValue>(this KeyValuePair<TKey, TValue> keyValuePair, out TKey key, out TValue value)
		{
			key = keyValuePair.Key;
			value = keyValuePair.Value;
		}

		public static KeyValuePair<TKey, TValue> ToKeyValuePair<TKey, TValue>(this (TKey, TValue) tuple) => Create(tuple.Item1, tuple.Item2);
	}
}

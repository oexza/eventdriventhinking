﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace EventDrivenThinking.Utils
{
    public class TypeCollection : IEnumerable<Type>, 
        IEquatable<TypeCollection>, 
        IReadOnlyCollection<Type>, 
        IReadOnlyList<Type>
    {
        private readonly Type[] _types;
        private readonly Lazy<Guid> _hash;
        
        public TypeCollection(IEnumerable<Type> types)
        {
            _types = types is Type[] ? (Type[])types :  types.Distinct().ToArray();
            _hash = new Lazy<Guid>(OnComputeHash);
        }
        public bool Contains<T>()
        {
            return Contains(typeof(T));
        }
        public bool Contains(Type t)
        {
            return _types.Contains(t);
        }
        public static explicit operator TypeCollection(Type[] data)
        {
            return new TypeCollection(data);
        }
        public static explicit operator TypeCollection(List<Type> data)
        {
            return new TypeCollection(data);
        }
        private Guid OnComputeHash()
        {
            return string.Concat(_types.Select(x => x.FullName).OrderBy(x => x)).ToGuid();
        }

        public IEnumerator<Type> GetEnumerator()
        {
            for (var index = 0; index < _types.Length; index++)
                yield return _types[index];
            
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public bool Equals(TypeCollection other)
        {
            if (other != null && _hash.Value == other._hash.Value)
                return true;
            return false;
        }

        public int Count => _types.Length;

        public Type this[int index] => _types[index];
    }
}
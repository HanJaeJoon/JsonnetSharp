// Ports the array builtins of go-jsonnet builtins.go: join, foldl, foldr, reverse, filter, member,
// sort (with Go's sort.Stable from sort/zsortinterface.go), range, minArray, maxArray, sum, avg,
// contains, remove and removeAt.

using System.Globalization;

namespace JsonnetSharp;

internal static partial class Builtins
{
    private static Value JoinArrays(Interpreter i, ValueArray sep, ValueArray arr)
    {
        List<CachedThunk> result = new(arr.Length());
        bool first = true;
        foreach (CachedThunk elem in arr.Elements)
        {
            Value elemValue = i.EvaluatePV(elem);
            switch (elemValue)
            {
                case ValueNull:
                    continue;
                case ValueArray v:
                    if (!first)
                    {
                        result.AddRange(sep.Elements);
                    }
                    result.AddRange(v.Elements);
                    break;
                default:
                    throw i.TypeErrorSpecific(elemValue, ValType.Array);
            }
            first = false;
        }
        return new ValueArray(result.ToArray());
    }

    private static Value JoinStrings(Interpreter i, ValueString sep, ValueArray arr)
    {
        List<int> result = new(arr.Length());
        bool first = true;
        foreach (CachedThunk elem in arr.Elements)
        {
            Value elemValue = i.EvaluatePV(elem);
            switch (elemValue)
            {
                case ValueNull:
                    continue;
                case ValueString v:
                    if (!first)
                    {
                        AddRunes(result, sep);
                    }
                    AddRunes(result, v);
                    break;
                default:
                    throw i.TypeErrorSpecific(elemValue, ValType.String);
            }
            first = false;
        }
        return new ValueFlatString(result.ToArray());
    }

    // Checked before flattening s: Go's lengths are 64-bit, a List<int> overflows.
    private static void AddRunes(List<int> result, ValueString s)
    {
        if ((long)result.Count + s.Length() > StringTooLongException.MaxRunes)
        {
            throw new StringTooLongException();
        }
        result.AddRange(s.GetRunes());
    }

    internal static Value BuiltinJoin(Interpreter i, Value sep, Value arrv)
    {
        ValueArray arr = i.GetArray(arrv);
        return sep switch
        {
            ValueString s => JoinStrings(i, s, arr),
            ValueArray a => JoinArrays(i, a, arr),
            _ => throw i.Error("join first parameter should be string or array, got " + sep.Type.Name),
        };
    }

    // The elements foldl/foldr iterate: an array's thunks, or one single-rune string per code point.
    private static CachedThunk[] FoldElements(Interpreter i, Value arrv, string name)
    {
        switch (arrv)
        {
            case ValueString s:
            {
                int[] runes = s.GetRunes();
                CachedThunk[] elements = new CachedThunk[runes.Length];
                for (int k = 0; k < runes.Length; k++)
                {
                    elements[k] = CachedThunk.ReadyThunk(new ValueFlatString(new[] { runes[k] }));
                }
                return elements;
            }
            case ValueArray a:
                return a.Elements;
            default:
                throw i.Error(name + " second parameter should be string or array, got " + arrv.Type.Name);
        }
    }

    internal static Value BuiltinFoldl(Interpreter i, Value funcv, Value arrv, Value initv)
    {
        ValueFunction fun = i.GetFunction(funcv);
        CachedThunk[] elements = FoldElements(i, arrv, "foldl");
        Value accValue = initv;
        for (int counter = 0; counter < elements.Length; counter++)
        {
            accValue = fun.Call(i, CallArguments.Args(CachedThunk.ReadyThunk(accValue), elements[counter]));
        }
        return accValue;
    }

    internal static Value BuiltinFoldr(Interpreter i, Value funcv, Value arrv, Value initv)
    {
        ValueFunction fun = i.GetFunction(funcv);
        CachedThunk[] elements = FoldElements(i, arrv, "foldr");
        Value accValue = initv;
        for (int counter = elements.Length - 1; counter >= 0; counter--)
        {
            accValue = fun.Call(i, CallArguments.Args(elements[counter], CachedThunk.ReadyThunk(accValue)));
        }
        return accValue;
    }

    internal static Value BuiltinReverse(Interpreter i, Value arrv)
    {
        ValueArray arr = i.GetArray(arrv);
        CachedThunk[] reversedArray = (CachedThunk[])arr.Elements.Clone();
        System.Array.Reverse(reversedArray);
        return new ValueArray(reversedArray);
    }

    internal static Value BuiltinFilter(Interpreter i, Value funcv, Value arrv)
    {
        ValueArray arr = i.GetArray(arrv);
        ValueFunction fun = i.GetFunction(funcv);
        int num = arr.Length();
        List<CachedThunk> elems = new(num);
        for (int counter = 0; counter < num; counter++)
        {
            Value includedValue = fun.Call(i, CallArguments.Args(arr.Elements[counter]));
            if (i.GetBoolean(includedValue).Value)
            {
                elems.Add(arr.Elements[counter]);
            }
        }
        return new ValueArray(elems.ToArray());
    }

    private static bool RawMember(Interpreter i, Value arrv, Value value)
    {
        switch (arrv)
        {
            case ValueString:
            {
                ValueString valString = i.GetString(value);
                ValueString arrString = i.GetString(arrv);
                return arrString.GetGoString().IndexOf(valString.GetGoString(), StringComparison.Ordinal) >= 0;
            }
            case ValueArray arr:
                foreach (CachedThunk elem in arr.Elements)
                {
                    if (RawEquals(i, elem.GetValue(i), value))
                    {
                        return true;
                    }
                }
                return false;
            default:
                throw i.Error("std.member first argument must be an array or a string");
        }
    }

    internal static Value BuiltinMember(Interpreter i, Value arrv, Value value) => MakeBool(RawMember(i, arrv, value));

    internal static Value BuiltinSort(Interpreter i, Value?[] arguments)
    {
        ValueArray arr = i.GetArray(arguments[0]!);
        ValueFunction keyF = i.GetFunction(arguments[1]!);
        int num = arr.Length();

        CachedThunk[] thunks = new CachedThunk[num];
        Value[] keys = new Value[num];
        for (int counter = 0; counter < num; counter++)
        {
            thunks[counter] = arr.Elements[counter];
            keys[counter] = keyF.Call(i, CallArguments.Args(arr.Elements[counter]));
        }

        // Go's sort.Stable; the comparison order matters for which type error is reported.
        new GoStableSort(i, thunks, keys).Sort();
        return new ValueArray(thunks);
    }

    // sort.Stable over sortData (insertion sort of 20-element blocks, then SymMerge).
    private sealed class GoStableSort
    {
        private readonly Interpreter _i;
        private readonly CachedThunk[] _thunks;
        private readonly Value[] _keys;

        public GoStableSort(Interpreter i, CachedThunk[] thunks, Value[] keys)
        {
            _i = i;
            _thunks = thunks;
            _keys = keys;
        }

        private bool Less(int a, int b) => ValueCmp(_i, _keys[a], _keys[b]) == -1;

        private void Swap(int a, int b)
        {
            (_thunks[a], _thunks[b]) = (_thunks[b], _thunks[a]);
            (_keys[a], _keys[b]) = (_keys[b], _keys[a]);
        }

        public void Sort()
        {
            int n = _thunks.Length;
            int blockSize = 20;
            int a = 0;
            int b = blockSize;
            while (b <= n)
            {
                InsertionSort(a, b);
                a = b;
                b += blockSize;
            }
            InsertionSort(a, n);

            while (blockSize < n)
            {
                a = 0;
                b = 2 * blockSize;
                while (b <= n)
                {
                    SymMerge(a, a + blockSize, b);
                    a = b;
                    b += 2 * blockSize;
                }
                int m = a + blockSize;
                if (m < n)
                {
                    SymMerge(a, m, n);
                }
                blockSize *= 2;
            }
        }

        private void InsertionSort(int a, int b)
        {
            for (int k = a + 1; k < b; k++)
            {
                for (int j = k; j > a && Less(j, j - 1); j--)
                {
                    Swap(j, j - 1);
                }
            }
        }

        private void SymMerge(int a, int m, int b)
        {
            if (m - a == 1)
            {
                int lo = m;
                int hi = b;
                while (lo < hi)
                {
                    int h = (int)((uint)(lo + hi) >> 1);
                    if (Less(h, a))
                    {
                        lo = h + 1;
                    }
                    else
                    {
                        hi = h;
                    }
                }
                for (int k = a; k < lo - 1; k++)
                {
                    Swap(k, k + 1);
                }
                return;
            }
            if (b - m == 1)
            {
                int lo = a;
                int hi = m;
                while (lo < hi)
                {
                    int h = (int)((uint)(lo + hi) >> 1);
                    if (!Less(m, h))
                    {
                        lo = h + 1;
                    }
                    else
                    {
                        hi = h;
                    }
                }
                for (int k = m; k > lo; k--)
                {
                    Swap(k, k - 1);
                }
                return;
            }

            int mid = (int)((uint)(a + b) >> 1);
            int n = mid + m;
            int start;
            int r;
            if (m > mid)
            {
                start = n - b;
                r = mid;
            }
            else
            {
                start = a;
                r = m;
            }
            int p = n - 1;
            while (start < r)
            {
                int c = (int)((uint)(start + r) >> 1);
                if (!Less(p - c, c))
                {
                    start = c + 1;
                }
                else
                {
                    r = c;
                }
            }

            int end = n - start;
            if (start < m && m < end)
            {
                Rotate(start, m, end);
            }
            if (a < start && start < mid)
            {
                SymMerge(a, start, mid);
            }
            if (mid < end && end < b)
            {
                SymMerge(mid, end, b);
            }
        }

        private void Rotate(int a, int m, int b)
        {
            int i = m - a;
            int j = b - m;
            while (i != j)
            {
                if (i > j)
                {
                    SwapRange(m - i, m, j);
                    i -= j;
                }
                else
                {
                    SwapRange(m - i, m + j - i, i);
                    j -= i;
                }
            }
            SwapRange(m - i, m, i);
        }

        private void SwapRange(int a, int b, int n)
        {
            for (int k = 0; k < n; k++)
            {
                Swap(a + k, b + k);
            }
        }
    }

    internal static Value BuiltinRange(Interpreter i, Value fromv, Value tov)
    {
        int from = i.GetInt(fromv);
        int to = i.GetInt(tov);
        long n = (long)to - from + 1;
        if (n <= 0)
        {
            return ValueArray.Empty;
        }
        CachedThunk[] elems = new CachedThunk[n];
        for (long k = 0; k < n; k++)
        {
            i.PollCancel(k);
            elems[k] = CachedThunk.ReadyThunk(new ValueNumber(from + k));
        }
        return new ValueArray(elems);
    }

    private static Value MinMaxArray(Interpreter i, Value?[] arguments, int wantSign)
    {
        ValueArray arr = i.GetArray(arguments[0]!);
        ValueFunction keyF = i.GetFunction(arguments[1]!);
        Value? onEmpty = arguments[2];
        int num = arr.Length();
        if (num == 0)
        {
            return onEmpty ?? throw i.Error("Expected at least one element in array. Got none");
        }
        Value bestVal = arr.Elements[0].GetValue(i);
        Value bestValKey = keyF.Call(i, CallArguments.Args(arr.Elements[0]));
        for (int index = 1; index < num; index++)
        {
            Value current = arr.Elements[index].GetValue(i);
            Value currentKey = keyF.Call(i, CallArguments.Args(arr.Elements[index]));
            int cmp = ValueCmp(i, bestValKey, currentKey);
            if (wantSign > 0 ? cmp > 0 : cmp < 0)
            {
                bestVal = current;
                bestValKey = currentKey;
            }
        }
        return bestVal;
    }

    internal static Value BuiltinMinArray(Interpreter i, Value?[] arguments) => MinMaxArray(i, arguments, 1);

    internal static Value BuiltinMaxArray(Interpreter i, Value?[] arguments) => MinMaxArray(i, arguments, -1);

    internal static Value BuiltinSum(Interpreter i, Value arrv)
    {
        ValueArray arr = i.GetArray(arrv);
        double sum = 0.0;
        foreach (CachedThunk elem in arr.Elements)
        {
            sum += i.EvaluateNumber(elem).Value;
        }
        return new ValueNumber(sum);
    }

    internal static Value BuiltinAvg(Interpreter i, Value arrv)
    {
        ValueArray arr = i.GetArray(arrv);
        double len = arr.Length();
        if (len == 0)
        {
            throw i.Error("Cannot calculate average of an empty array.");
        }
        double sum = i.GetNumber(BuiltinSum(i, arrv)).Value;
        return new ValueNumber(sum / len);
    }

    internal static Value BuiltinContains(Interpreter i, Value arrv, Value ev)
    {
        ValueArray arr = i.GetArray(arrv);
        foreach (CachedThunk elem in arr.Elements)
        {
            if (RawEquals(i, elem.GetValue(i), ev))
            {
                return ValueBoolean.True;
            }
        }
        return ValueBoolean.False;
    }

    internal static Value BuiltinRemove(Interpreter i, Value arrv, Value ev)
    {
        ValueArray arr = i.GetArray(arrv);
        for (int idx = 0; idx < arr.Elements.Length; idx++)
        {
            if (RawEquals(i, arr.Elements[idx].GetValue(i), ev))
            {
                return BuiltinRemoveAt(i, arrv, new ValueNumber(idx));
            }
        }
        return arr;
    }

    internal static Value BuiltinRemoveAt(Interpreter i, Value arrv, Value idxv)
    {
        ValueArray arr = i.GetArray(arrv);
        int idx = i.GetInt(idxv);
        int len = arr.Elements.Length;

        // Go slices arr.elements[:idx] and [idx+1:] and panics when idx is out of range.
        if (idx < 0)
        {
            throw GoPanic("runtime error: slice bounds out of range [:" + idx.ToString(CultureInfo.InvariantCulture) + "]");
        }
        if (idx > len)
        {
            throw GoPanic("runtime error: slice bounds out of range [:" + idx.ToString(CultureInfo.InvariantCulture) +
                "] with capacity " + len.ToString(CultureInfo.InvariantCulture));
        }
        if (idx + 1 > len)
        {
            throw GoPanic("runtime error: slice bounds out of range [" + (idx + 1).ToString(CultureInfo.InvariantCulture) +
                ":" + len.ToString(CultureInfo.InvariantCulture) + "]");
        }
        CachedThunk[] newArr = new CachedThunk[len - 1];
        System.Array.Copy(arr.Elements, newArr, idx);
        System.Array.Copy(arr.Elements, idx + 1, newArr, idx, len - idx - 1);
        return new ValueArray(newArr);
    }

    // A Go runtime panic; the VM reports it as an INTERNAL ERROR (CRASH) like go-jsonnet.
    private static Exception GoPanic(string message) => new InvalidOperationException(message);
}

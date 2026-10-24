namespace FastPsd.Core.Psd;

/// <summary>
/// Photoshop の ActionDescriptor（レイヤー効果 lfx2 などで使われる構造化データ）の最小限のリーダー。
/// 値は Dictionary / List / プリミティブに変換する。
/// </summary>
public static class Descriptor
{
    public static Dictionary<string, object?> Read(BeReader r)
    {
        r.UnicodeString(); // 名前
        ClassId(r);        // クラスID
        uint n = r.U32();
        if (n > 100_000) throw new InvalidDataException("descriptor item count");
        var d = new Dictionary<string, object?>();
        for (uint i = 0; i < n; i++)
        {
            string key = ClassId(r);
            string type = r.Ascii4();
            d[key] = Value(r, type);
        }
        return d;
    }

    static string ClassId(BeReader r)
    {
        uint len = r.U32();
        int l = len == 0 ? 4 : (int)len;
        if (l > 10_000) throw new InvalidDataException("classID length");
        return System.Text.Encoding.ASCII.GetString(r.Bytes(l));
    }

    static object? Value(BeReader r, string type)
    {
        switch (type)
        {
            case "Objc":
            case "GlbO":
                return Read(r);
            case "obj ":
                SkipReference(r);
                return null;
            case "VlLs":
                {
                    uint n = r.U32();
                    var list = new List<object?>((int)Math.Min(n, 1024));
                    for (uint i = 0; i < n; i++)
                    {
                        string t = r.Ascii4();
                        list.Add(Value(r, t));
                    }
                    return list;
                }
            case "doub": return r.F64();
            case "UntF":
                r.Ascii4();
                return r.F64();
            case "UnFl":
                {
                    r.Ascii4();
                    uint n = r.U32();
                    r.Skip(n * 8L);
                    return null;
                }
            case "TEXT": return r.UnicodeString();
            case "enum":
                {
                    string t = ClassId(r);
                    string e = ClassId(r);
                    return t + "." + e;
                }
            case "long": return r.I32();
            case "comp": return r.I64();
            case "bool": return r.U8() != 0;
            case "type":
            case "GlbC":
                r.UnicodeString();
                return ClassId(r);
            case "alis":
            case "tdta":
            case "Pth ":
                {
                    uint len = r.U32();
                    r.Skip(len);
                    return null;
                }
            default:
                throw new NotSupportedException($"未対応のdescriptor型: '{type}'");
        }
    }

    static void SkipReference(BeReader r)
    {
        uint n = r.U32();
        for (uint i = 0; i < n; i++)
        {
            string t = r.Ascii4();
            switch (t)
            {
                case "prop": r.UnicodeString(); ClassId(r); ClassId(r); break;
                case "Clss": r.UnicodeString(); ClassId(r); break;
                case "Enmr": r.UnicodeString(); ClassId(r); ClassId(r); ClassId(r); break;
                case "rele": r.UnicodeString(); ClassId(r); r.I32(); break;
                case "Idnt":
                case "indx": r.I32(); break;
                case "name": r.UnicodeString(); ClassId(r); r.UnicodeString(); break;
                default: throw new NotSupportedException($"未対応のreference型: '{t}'");
            }
        }
    }
}

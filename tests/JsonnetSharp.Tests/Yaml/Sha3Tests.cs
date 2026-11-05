using System.Text;

namespace JsonnetSharp.Tests;

// Expected digests come from std.sha3 in the go-jsonnet 0.22.0 binary (the first two are also the FIPS 202 vectors).
public class Sha3Tests
{
    [Theory]
    [InlineData("", "a69f73cca23a9ac5c8b567dc185a756e97c982164fe25859e0d1dcc1475c80a615b2123af1f5f94c11e3e9402c3ac558f500199d95b6d3e301758586281dcd26")]
    [InlineData("abc", "b751850b1a57168a5693cd924b6b096e08f621827444f70d884f5d0240d2712e10e116e9192af3c91a7ec57647e3934057340b4cf408d5a56592f8274eec53f0")]
    [InlineData("a*71", "070faf98d2a8fddf8ed886408744dc06456096c2e045f26f3c7b010530e6bbb3db535a54d636856f4e0e1e982461cb9a7e8e57ff8895cff1619af9f0e486e28c")]
    [InlineData("a*72", "a8ae722a78e10cbbc413886c02eb5b369a03f6560084aff566bd597bb7ad8c1ccd86e81296852359bf2faddb5153c0a7445722987875e74287adac21adebe952")]
    [InlineData("a*73", "23e6a8815f8201dbbf6a5463be8dcadb1acea9df5f8998954e59ac9565cf6d29b17aa27a5e8b0fc06343db6122d6e544d27583ddc78504d08203217e7e65b6bd")]
    [InlineData("a*143", "1dfc536c0ef79e004ec6f18e3b24fd6c4c3076556424ef369e8734312d6594ff9b92a8f02d2980ab51c191a9cc3cf47d06265e81d306d4098cdf2b6bada1db27")]
    [InlineData("a*144", "446cd4d7ba19510dcc776b21045bc68d424b5b840e14685e149bb238b5f473c0356b69e04f0f5785eefce20ff09e678b080d8aac64568c5edf001cd32b2ed7a8")]
    [InlineData("a*145", "84a7b171615f4f0024b772defd5ea21a536bb1e52306fa7ad412c532ac919f6b645e412aa7f5d979808c8ae03ca7d159363dcdb179c9b03f908e3b3526cbf4de")]
    [InlineData("héllo 中\U0001F600", "14b97f1fb9ed9581c86febb967a3603d68b047c0d2c7817ff43831e71ee849a21d80bcec3cc7f4b55fc64b2364ebc167a1f94bb5b2a03fd64be827512fcdf65a")]
    [InlineData("a*1000", "ac7e95cc95aa7f24aaa95e040ca0c79b39cd9cc84a10abb84ddd8dd5e4b45cf96543aaa70d0ef99fbf8d2769639981ee1fd0b0276f4756b9d504d0b7de19b700")]
    [InlineData("The quick brown fox jumps over the lazy dog", "01dedd5de4ef14642445ba5f5b97c15e47b9ad931326e4b0727cd94cefc44fff23f07bf543139939b49128caf436dc1bdee54fcb24023a08d9403f9b4bf0d450")]
    public void Sum512(string input, string hex)
    {
        if (input.StartsWith("a*", StringComparison.Ordinal))
        {
            input = new string('a', int.Parse(input.Substring(2)));
        }
        byte[] digest = Sha3.Sum512(Encoding.UTF8.GetBytes(input));
        Assert.Equal(hex, string.Concat(digest.Select(b => b.ToString("x2"))));
    }
}

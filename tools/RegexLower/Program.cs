using System.Text;
using Apps2Samsung.Helpers.Core;

var input = await new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false)).ReadToEndAsync();
await using var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
await stdout.WriteAsync(RegexPropertyEscapes.Lower(input));

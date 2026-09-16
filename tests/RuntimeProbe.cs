using System;
using System.Text;
class RuntimeProbe {
 static int Main(string[] args) {
  foreach(string key in new[]{"HTTP_PROXY","HTTPS_PROXY","ALL_PROXY"})
   if(Environment.GetEnvironmentVariable(key)!="http://127.0.0.1:17896")return 81;
  if(Environment.GetEnvironmentVariable("NO_PROXY")!="localhost,127.0.0.1")return 82;
  if(args.Length!=3||args[0]!="space value"||args[1]!="quote\"value"||args[2]!="trailing\\")return 83;
  var reader=new System.IO.StreamReader(Console.OpenStandardInput(),Encoding.UTF8);
  var output=Console.OpenStandardOutput();
  var first=Encoding.UTF8.GetBytes(reader.ReadLine()+"\n");output.Write(first,0,first.Length);output.Flush();
  var input=reader.ReadToEnd();
  var bytes=Encoding.UTF8.GetBytes(input);
  Console.OpenStandardOutput().Write(bytes,0,bytes.Length);
  Console.Error.Write("probe-stderr");return 37;
 }
}

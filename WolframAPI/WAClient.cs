﻿using WolframAPI.Exceptions;

namespace WolframAPI
{
	using System;
	using System.IO;
	using System.Linq;
	using System.Net;
	using System.Reflection;
	using System.Runtime.Remoting.Messaging;
	using System.Web;
	using System.Xml.Serialization;

	/// <summary>
	/// Used to handle the response received event which occurs when a response is successfully
	/// retrieved using the API.
	/// </summary>
	/// <param name="response">The (raw) response returned.</param>
	/// <param name="expression">The expression submitted.</param>
    public delegate void SolutionReceivedEventHandler (string response, string expression = "");
	/// <summary>
	/// Used to handle the result response which is used in the <see cref="WAClient.GetResult"/> method.
	/// </summary>
	/// <param name="result">The result received</param>
	/// <param name="expression">The expression submitted</param>
    public delegate void ResultReceivedEventHandler (WAResult result, string expression = "");
	/// <summary>
	/// Used to asynchronously call the expression processor methods.
	/// <param name="expression">The expression submitted.</param>
	/// </summary>
    public delegate string ExpressionProcessorMethod (string expression);
	/// <summary>
	/// Used to asynchronously fetch the result from Wolfram.
	/// </summary>
	/// <param name="expression">Expression to submit.</param>
	/// <returns>The fetched result.</returns>
    public delegate WAResult RetrieveResultMethod (string expression);
	/// <summary>
	/// Used to access Wolfram Alpha. 
	/// Submits expressions, retrieves and parses responses.
	/// </summary>
	public sealed class WAClient
	{
		/// <summary>
		/// The base WA API url.
		/// </summary>
		public const string BaseUrl =
            "http://api.wolframalpha.com/v2/query?appid={0}&input={1}&format=image,plaintext";
		/// <summary>
		/// The application ID.
		/// </summary>
		private readonly string _appId;

		/// <summary>
		/// Occurs when a response is successfully retrieved using the API.
		/// </summary>
		public event SolutionReceivedEventHandler OnSolutionReceived;
		/// <summary>
		/// Occurs when a result is successfully retrieved using the API.
		/// </summary>
		public event ResultReceivedEventHandler OnResultReceived;

		private static readonly object SyncLock = new object ();

		/// <summary>
		/// Gets the version of the currently used library.
		/// </summary>
		/// <value>The version.</value>
		public static string Version {
			get {
				return (Assembly.GetExecutingAssembly ().GetName ().Version.ToString ());
			}
		}

		/// <summary>
		/// Initializes a new instance of the <see cref="WAClient"/> class.
		/// </summary>
		/// <param name="appId">The application ID provided by Wolfram. You have to request one for each of your apps.</param>
		public WAClient (string appId)
		{
			_appId = appId;
		}

		/// <summary>
		/// Solves the specified expression.
		/// </summary>
		/// <param name="expression">The expression.</param>
		/// <exception cref="WolframException">Throws in case of any error.</exception>
		/// <exception cref="ArgumentNullException">Throws if the specified argument is null.</exception>
		/// <returns>The solution of the given expression</returns>
		public string Solve (string expression)
		{
			if (string.IsNullOrEmpty (expression))
				throw new ArgumentNullException ("expression", "Cannot evaluate a null string.");
			var response = Submit (expression);
			var result = Parse (response);
			if (result.Pods == null || result.Pods.Count <= 0) {
				return "No solution found. The response might have been malformed.";
			}
			var solution = (
				from pod in result.Pods
				where pod.Title.ToLower ().Contains ("solution") 
				|| pod.Title.ToLower ().Contains ("result") 
				|| pod.Title.ToLower ().Contains ("derivative") 
				|| pod.Title.ToLower ().Contains ("decimal form") 
				|| pod.Title.ToLower ().Contains ("decimal approximation")
				select pod).FirstOrDefault ();
			if (solution == null) {
				return "No solution found.";
			}
			if (solution.SubPods == null || solution.SubPods.Count <= 0) {
				return "No solution found. The response might have been malformed.";
			}
			if (string.IsNullOrEmpty (solution.SubPods [0].PlainText)) {
				return "No solution found. The pod order might have changed. Report to devs!";
			}
			return solution.SubPods [0].PlainText;
		}

		/// <summary>
		/// Gets the result of the specified expression.
		/// <para>The expression is returned as <see cref="WAResult"/></para> 
		/// so you can manually go through the pods of the response (to get ANY information you'd like)
		/// <para>It is encouraged to use this method instead of <see cref="Solve"/></para>
		/// </summary>
		/// <param name="expression">The expression to solve.</param>
		/// <exception cref="WolframException">Throws in case of any error.</exception>
		/// <exception cref="ArgumentNullException">Throws if the specified argument is null.</exception>
		/// <returns>The result</returns>
		public WAResult GetResult (string expression)
		{
			if (string.IsNullOrEmpty (expression)) {
				throw new ArgumentNullException ("expression", "The parameter passed to this method was null or empty.");
			}

			var response = Submit (expression);

			var result = Parse (response);

			return result;
		}

		/// <summary>
		/// Submits the specified expression and returns the raw result.
		/// </summary>
		/// <param name="expression">The expression to post.</param>
		/// <exception cref="WolframException">Throws in case of any error.</exception>
		/// <exception cref="ArgumentNullException">Throws if the specified argument is null.</exception>
		/// <returns>Raw response</returns>
		public string Submit (string expression)
		{
			if (string.IsNullOrEmpty (expression))
				throw new ArgumentNullException ("expression", "Cannot evaluate a null string.");

			try {
				expression = expression.Replace ("=", " = ");
				var wacode = HttpUtility.UrlEncode (expression);

				var url = new Uri (string.Format (BaseUrl, _appId, wacode));

				string returned;

				using (var client = new WebClient()) {
					returned = client.DownloadString (url);
				}

				if (!string.IsNullOrEmpty (returned)) {
					return returned;
				}

				return "Couldn't retrieve information!";
			} catch (WebException x) {
				throw new WolframException ("WebException thrown while getting the response.", x);
			} catch (Exception x) {
				throw new WolframException ("Unhandled exception thrown while submitting the expression.", x);
			}
		}

		/// <summary>
		/// Parses the raw response.
		/// </summary>
		/// <param name="response">The response to parse</param>
		/// <returns>The parsed response</returns>
		/// <exception cref="WolframException">Throws in case of any error.</exception>
		/// <exception cref="ArgumentNullException">Throws if the specified argument is null.</exception>
		public static WAResult Parse (string response)
		{
			if (string.IsNullOrEmpty (response))
				throw new ArgumentNullException ("response", "Cannot evaluate a null string.");

			try {
				var serializer = new XmlSerializer (typeof(WAResult));

				WAResult result;

				using (var reader = new StringReader(response)) {
					result = serializer.Deserialize (reader) as WAResult;
				}

				if (result == null) {
					throw new WolframException ("Could not deserialize the response. It might have been a malformed one.");
				}

				return result;
			} catch (InvalidOperationException x) {
				throw new WolframException ("Exception thrown while deserializing the response.", x);
			}
		}

		#region Async stuff

		#if WITH_ASYNC
		private void HandleSolutionReceived (IAsyncResult ar)
		{
			if (ar == null || ((AsyncResult)ar).AsyncDelegate == null)
				throw new ArgumentNullException ("ar");

			if (ar != null) {
				var expr = ar.AsyncState as string;

				var proc = (ExpressionProcessorMethod)(((AsyncResult)ar).AsyncDelegate);

				if (proc != null) {
					var solution = proc.EndInvoke (ar);

					if (OnSolutionReceived != null) {
						OnSolutionReceived (solution, expr);
					}
				}
			}
		}

		/// <summary>
		/// Solves the specified expression asynchronously.
		/// <remarks>An event is raised on completion.</remarks>
		/// </summary>
		/// <param name="expression">The expression.</param>
		/// <exception cref="WolframException">Throws in case of any error.</exception>
		/// <exception cref="ArgumentNullException">Throws if the specified argument is null.</exception>
		/// <returns>The solution of the given expression</returns>
		public void SolveAsync (string expression)
		{
			if (string.IsNullOrEmpty (expression))
				throw new ArgumentNullException ("expression", "Cannot evaluate a null string.");

			var procedure = new ExpressionProcessorMethod (Solve);

			lock (SyncLock) {
				procedure.BeginInvoke (expression, HandleSolutionReceived, expression);
			}
		}

		private void HandleResultReceived (IAsyncResult ar)
		{
			if (ar == null || ((AsyncResult)ar).AsyncDelegate == null)
				throw new ArgumentNullException ("ar");

			var expr = ar.AsyncState as string;

			var proc = (RetrieveResultMethod)(((AsyncResult)ar).AsyncDelegate);

			if (proc != null) {
				var result = proc.EndInvoke (ar);

				if (OnResultReceived != null) {
					OnResultReceived (result, expr);
				}
			}
			
		}

		/// <summary>
		/// Gets the result of the specified expression asynchronously.
		/// <para>The expression is returned as <see cref="WAResult"/></para> 
		/// so you can manually go through the pods of the response (to get ANY information you'd like)
		/// <para>It is encouraged to use this method instead of <see cref="Solve"/></para>
		/// <remarks>An event is raised upon completion.</remarks>
		/// </summary>
		/// <param name="expression">The expression to solve.</param>
		/// <exception cref="WolframException">Throws in case of any error.</exception>
		/// <exception cref="ArgumentNullException">Throws if the specified argument is null.</exception>
		/// <returns>The result</returns>
		public void GetResultAsync (string expression)
		{
			if (string.IsNullOrEmpty (expression))
				throw new ArgumentNullException ("expression", "Cannot evaluate a null string.");

			var procedure = new RetrieveResultMethod (GetResult);

			lock (SyncLock) {
				procedure.BeginInvoke (expression, HandleResultReceived, expression);
			}
		}

		#endif
		#endregion

	}
}

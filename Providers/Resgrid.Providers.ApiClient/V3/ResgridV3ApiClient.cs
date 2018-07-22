using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;

namespace Resgrid.Providers.ApiClient.V3
{
	public class ResgridV3ApiClient
	{
		private static HttpClient _client;
		private static string _baseUrl;
		private static string _userName;
		private static string _password;
		private static string _token;
		private static DateTime _tokenExpiry;
		private const string _baseApiUrl = "/api/v3/";

		public static void Init(string baseUrl, string userName, string password)
		{
			_client = HttpClientFactory.Create();
			_client.BaseAddress = new Uri(baseUrl);
			_client.DefaultRequestHeaders.Accept.Clear();
			_client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

			_userName = userName;
			_password = password;
		}

		public static async Task<bool> Auth()
		{
			Func<Task<bool>> getSetAuthToken = async () =>
			{
				HttpResponseMessage response = await _client.PostAsJsonAsync($"{_baseApiUrl}Auth/Validate", new
				{
					Usr = _userName,
					Pass = _password,
				});

				if (!response.IsSuccessStatusCode)
					return false;

				var result = await response.Content.ReadAsAsync<Models.ValidateResult>();

				if (result == null || String.IsNullOrWhiteSpace(result.Tkn))
					return false;

				_token = result.Tkn;
				_tokenExpiry = DateTime.Parse(result.Txd);

				_client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", _token);

				return true;
			};

			if (String.IsNullOrWhiteSpace(_token))
			{
				return await getSetAuthToken();
			}
			else
			{
				if (_tokenExpiry <= DateTime.UtcNow)
				{
					return await getSetAuthToken();
				}

				return true;
			}
		}

		public static async Task<T> Get<T>(string url) where T : class
		{
			if (await Auth())
			{
				var res = await _client.GetAsync(new Uri($"{_baseApiUrl}{url}", UriKind.Relative));
				res.EnsureSuccessStatusCode();

				return await res.Content.ReadAsAsync<T>();
			}

			throw new Exception("Unable to GET, Auth call failed");
		}

		public static async Task<T> Post<T>(string url, object data) where T : class
		{
			if (await Auth())
			{
				var res = await _client.PostAsJsonAsync(new Uri($"{_baseApiUrl}{url}", UriKind.Relative), data);
				res.EnsureSuccessStatusCode();

				return await res.Content.ReadAsAsync<T>();
			}

			throw new Exception("Unable to POST, Auth call failed");
		}
	}
}

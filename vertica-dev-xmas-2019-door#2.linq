<Query Kind="Program">
  <Reference>&lt;RuntimeDirectory&gt;\System.Net.Http.dll</Reference>
  <NuGetReference>Microsoft.AspNet.WebApi.Client</NuGetReference>
  <NuGetReference>NEST</NuGetReference>
  <NuGetReference>Newtonsoft.Json</NuGetReference>
  <Namespace>Elasticsearch.Net</Namespace>
  <Namespace>Nest</Namespace>
  <Namespace>Newtonsoft.Json</Namespace>
  <Namespace>Newtonsoft.Json.Bson</Namespace>
  <Namespace>Newtonsoft.Json.Converters</Namespace>
  <Namespace>Newtonsoft.Json.Linq</Namespace>
  <Namespace>Newtonsoft.Json.Schema</Namespace>
  <Namespace>Newtonsoft.Json.Serialization</Namespace>
  <Namespace>System.Net.Http</Namespace>
  <Namespace>System.Net.Http.Formatting</Namespace>
  <Namespace>System.Net.Http.Handlers</Namespace>
  <Namespace>System.Net.Http.Headers</Namespace>
  <Namespace>System.Threading.Tasks</Namespace>
</Query>

async Task Main()
{
	using (var httpClient = new HttpClient()) // keep as singleton, dispose once your application shuts down
	{
		httpClient.BaseAddress = new Uri("https://vertica-xmas2019.azurewebsites.net");

		HttpResponseMessage apiResponse = await httpClient.PostAsJsonAsync("/api/participate", new { fullName = "", emailAddress = "", subscribeToNewsletter = true });

		if (!apiResponse.IsSuccessStatusCode)
			throw new InvalidOperationException($"{apiResponse.StatusCode}: {(await apiResponse.Content.ReadAsStringAsync())}");

		var participateResponse = await apiResponse.Content.ReadAsAsync<ParticipateResponse>();

		IElasticClient elasticClient = CreateElasticClient(participateResponse.Credentials.Username, participateResponse.Credentials.Password);

		GetResponse<SantaTracking> santaTrackingResponse = await elasticClient.GetAsync<SantaTracking>(participateResponse.Id, selector => selector.Index("santa-trackings"));

		if (!santaTrackingResponse.IsValid)
			throw new InvalidOperationException($"{santaTrackingResponse.DebugInformation}");

		GeoPoint santaPosition = santaTrackingResponse.Source.CalculateSantaPosition();

		apiResponse = await httpClient.PostAsJsonAsync("/api/santarescue", new { id = participateResponse.Id, position = santaPosition });

		if (!apiResponse.IsSuccessStatusCode)
			throw new InvalidOperationException($"{apiResponse.StatusCode}: {(await apiResponse.Content.ReadAsStringAsync())}");
	}
}

public class SantaTracking
{
	public GeoPoint CanePosition { get; set; }
	public Movement[] SantaMovements { get; set; }

	public GeoPoint CalculateSantaPosition()
	{
		double x = 0d;
		double y = 0d;

		foreach (Movement movement in SantaMovements)
		{
			double meters = movement.Unit.ToMeters(movement.Value);

			switch (movement.Direction)
			{
				case Direction.up:
					y += meters;
					break;
				case Direction.right:
					x += meters;
					break;
				case Direction.down:
					y -= meters;
					break;
				case Direction.left:
					x -= meters;
					break;
				default:
					throw new ArgumentOutOfRangeException();
			}
		}

		return CanePosition.MoveTo(x, y);
	}
}

public enum Direction
{
	up, 
	down, 
	left, 
	right
}

public class GeoPoint
{
	public GeoPoint(double lat, double lon)
	{
		Lat = lat;
		Lon = lon;
	}
	
	public double Lat { get; }
	public double Lon { get; }

	/// <summary>
	/// https://stackoverflow.com/questions/7477003/calculating-new-longitude-latitude-from-old-n-meters
	/// </summary>
	public GeoPoint MoveTo(double xMeters, double yMeters)
	{
		return new GeoPoint(
			MoveLatitude(Lat, yMeters),
			MoveLongitude(Lon, Lat, xMeters));

		double MoveLatitude(double latitude, double meters)
		{
			if (meters.Equals(0d))
				return latitude;

			return latitude + meters * M;
		}

		double MoveLongitude(double longitude, double latitude, double meters)
		{
			if (meters.Equals(0d))
				return longitude;

			return longitude + meters * M / Math.Cos(latitude * (Math.PI / 180));
		}
	}

	/// <summary>
	/// 1 meter in degree (with radius of the earth in kilometer)
	/// </summary>
	private const double M = 1 / (2 * Math.PI / 360 * 6378.137) / 1000;
}

public class Movement
{
	public Direction Direction { get; set; }
	public double Value { get; set; }
	public Unit Unit { get; set; }	
}

public class ParticipateResponse
{
	public Guid Id { get; set; }
	public Credentials Credentials { get; set; }
}

public class Credentials
{
	public string Username { get; set; }
	public string Password { get; set; }
}

private static IElasticClient CreateElasticClient(string username, string password)
{
	var settings = new ConnectionSettings("xmas2019:ZXUtY2VudHJhbC0xLmF3cy5jbG91ZC5lcy5pbyRlZWJjNmYyNzcxM2Q0NTE5OTcwZDc1Yzg2MDUwZTM2MyQyNDFmMzQ3OWNkNzg0ZTUyOTRkODk5OTViMjg0MjAyYg==",
		new BasicAuthenticationCredentials(username, password));
		
	return new ElasticClient(settings);
}

public static class Lengths
{
	public static double MetersToKilometers(this double meters)
	{
		return meters / 1000;
	}

	public static double KilometersToMeters(this double kilometers)
	{
		return kilometers * 1000;
	}

	public static double MetersToFeet(this double meters)
	{
		return meters / 0.304800610;
	}

	public static double FeetToMeters(this double feet)
	{
		return feet * 0.304800610;
	}

	public static double ToMeters(this Unit unit, double value)
	{
		switch (unit)
		{
			case Unit.foot:
				return value.FeetToMeters();

			case Unit.meter:
				return value;

			case Unit.kilometer:
				return value.KilometersToMeters();

			default:
				throw new ArgumentOutOfRangeException(nameof(unit), unit, null);
		}
	}

	public static double FromMeters(this double meters, Unit toUnit)
	{
		switch (toUnit)
		{
			case Unit.foot:
				return meters.MetersToFeet();
			case Unit.meter:
				return meters;
			case Unit.kilometer:
				return meters.MetersToKilometers();
			default:
				throw new ArgumentOutOfRangeException(nameof(toUnit), toUnit, null);
		}
	}
}

public enum Unit
{
	foot, 
	meter, 
	kilometer
}
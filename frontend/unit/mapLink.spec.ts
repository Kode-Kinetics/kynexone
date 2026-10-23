import { expect, test } from '@playwright/test';
import { googleMapsUrl, isInGcc, looksSwapped, parseLocationInput } from '../src/lib/mapLink';

const at = (latitude: number, longitude: number, from: 'link' | 'text') => ({ kind: 'coordinates', latitude, longitude, from });

test('Google Maps links in every shape the Share and address bar give', () => {
  expect(parseLocationInput('https://www.google.com/maps/@24.7136,46.6753,15z')).toEqual(at(24.7136, 46.6753, 'link'));
  expect(parseLocationInput('https://www.google.com/maps?q=21.5433,39.1728')).toEqual(at(21.5433, 39.1728, 'link'));
  expect(parseLocationInput('https://maps.google.com/?ll=26.2235,50.5876&z=12')).toEqual(at(26.2235, 50.5876, 'link'));
  expect(parseLocationInput('https://www.google.com/maps/search/?api=1&query=25.2854%2C51.5310')).toEqual(at(25.2854, 51.531, 'link'));
  expect(parseLocationInput('https://www.google.com/maps/search/21.5433,+39.1728?entry=tts')).toEqual(at(21.5433, 39.1728, 'link'));
  // A place link: the place's own pin (!3d!4d) wins over the viewport centre (@).
  expect(parseLocationInput('https://www.google.com/maps/place/Kingdom+Centre/@24.7114,46.6744,17z/data=!3m1!4b1!4m6!3m5!8m2!3d24.7113741!4d46.6743887'))
    .toEqual(at(24.711374, 46.674389, 'link'));
  expect(parseLocationInput('https://www.google.com/maps/place/Riyadh/@24.7136,46.6753,11z')).toEqual(at(24.7136, 46.6753, 'link'));
  expect(parseLocationInput('www.google.com.sa/maps/@24.7,46.6,12z')).toEqual(at(24.7, 46.6, 'link'));
  expect(parseLocationInput('https://www.openstreetmap.org/?mlat=24.7136&mlon=46.6753#map=17/24.7136/46.6753')).toEqual(at(24.7136, 46.6753, 'link'));
  expect(parseLocationInput('https://www.openstreetmap.org/#map=17/24.71360/46.67530')).toEqual(at(24.7136, 46.6753, 'link'));
  expect(parseLocationInput('https://maps.apple.com/?ll=24.7136,46.6753&q=Office')).toEqual(at(24.7136, 46.6753, 'link'));
  // A lookalike host is not OpenStreetMap.
  expect(parseLocationInput('https://evilopenstreetmap.org/?mlat=24.7136&mlon=46.6753')).not.toEqual(at(24.7136, 46.6753, 'link'));
});

test('short links go to the backend resolver, as https', () => {
  expect(parseLocationInput('https://maps.app.goo.gl/AbC123xyz')).toEqual({ kind: 'short-link', url: 'https://maps.app.goo.gl/AbC123xyz' });
  expect(parseLocationInput('maps.app.goo.gl/AbC123xyz')).toEqual({ kind: 'short-link', url: 'https://maps.app.goo.gl/AbC123xyz' });
  expect(parseLocationInput('http://goo.gl/maps/xYz987')).toEqual({ kind: 'short-link', url: 'https://goo.gl/maps/xYz987' });
  expect(parseLocationInput('https://goo.gl/other')).toEqual({ kind: 'unreadable' });
});

test('plain coordinates, with hemispheres, Arabic digits and degrees-minutes-seconds', () => {
  expect(parseLocationInput('21.5433, 39.1728')).toEqual(at(21.5433, 39.1728, 'text'));
  expect(parseLocationInput('21.5433,39.1728')).toEqual(at(21.5433, 39.1728, 'text'));
  expect(parseLocationInput('  21.5433   39.1728 ')).toEqual(at(21.5433, 39.1728, 'text'));
  expect(parseLocationInput('24.7136 N, 46.6753 E')).toEqual(at(24.7136, 46.6753, 'text'));
  expect(parseLocationInput('33.8688 S, 151.2093 E')).toEqual(at(-33.8688, 151.2093, 'text'));
  expect(parseLocationInput('٢١٫٥٤٣٣، ٣٩٫١٧٢٨')).toEqual(at(21.5433, 39.1728, 'text'));
  expect(parseLocationInput(`21°32'35.9"N 39°10'22.1"E`)).toEqual(at(21.543306, 39.172806, 'text'));
  expect(parseLocationInput('21°32′35.9″N, 39°10′22.1″E')).toEqual(at(21.543306, 39.172806, 'text'));
  expect(parseLocationInput('N 24° 42\' 49" E 46° 40\' 31"')).toEqual(at(24.713611, 46.675278, 'text'));
  expect(parseLocationInput(`39°10'22.1"E 21°32'35.9"N`)).toEqual(at(21.543306, 39.172806, 'text'));
});

test('anything else is unreadable, never guessed', () => {
  for (const input of ['', 'Riyadh', '24.7', '95, 46', '24.7, 200', '24.7, 46.6, 3', 'https://example.com/?q=24.7,46.6',
    'https://www.google.com/maps/place/Riyadh', `21°75'00"N 39°10'00"E`, `21°32'N 39°10'S`, 'javascript:alert(1)'])
    expect(parseLocationInput(input), input).toEqual({ kind: 'unreadable' });
});

test('the GCC box warns, not blocks, and spots a swapped pair', () => {
  expect(isInGcc(24.7136, 46.6753)).toBe(true); // Riyadh
  expect(isInGcc(17.0151, 54.0924)).toBe(true); // Salalah
  expect(isInGcc(29.3759, 47.9774)).toBe(true); // Kuwait City
  expect(isInGcc(51.5072, -0.1276)).toBe(false); // London
  expect(looksSwapped(46.6753, 24.7136)).toBe(true);
  expect(looksSwapped(24.7136, 46.6753)).toBe(false);
  expect(googleMapsUrl(24.7136, 46.6753)).toBe('https://www.google.com/maps/search/?api=1&query=24.7136,46.6753');
});

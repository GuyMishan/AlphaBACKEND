/**
 * Alpha OTP mail gateway. Deploy as a web app that executes as you.
 * Set ALPHA_OTP_SECRET in Script Properties; never place it in this file.
 */
function doPost(e) {
  try {
    if (!e || !e.postData || e.postData.contents.length > 1024) return reply_(false);
    const body = JSON.parse(e.postData.contents);
    const secret = PropertiesService.getScriptProperties().getProperty('ALPHA_OTP_SECRET');
    if (!secret || secret.length < 32 || typeof body.secret !== 'string' || !sameSecret_(secret, body.secret))
      return reply_(false);
    if (typeof body.to !== 'string' || !/^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(body.to) || body.to.length > 320)
      return reply_(false);
    if (typeof body.code !== 'string' || !/^\d{6}$/.test(body.code)) return reply_(false);

    MailApp.sendEmail({
      to: body.to,
      subject: 'קוד כניסה למערכת Alpha',
      body: 'קוד הכניסה שלך: ' + body.code + '\nהקוד תקף ל-5 דקות.',
      name: 'Alpha'
    });
    return reply_(true);
  } catch (error) {
    // Do not log recipient addresses, codes, or the shared secret.
    console.error('OTP mail delivery failed');
    return reply_(false);
  }
}

function sameSecret_(expected, received) {
  // Avoid early exits based on the first differing character.
  let difference = expected.length ^ received.length;
  for (let i = 0; i < Math.max(expected.length, received.length); i++)
    difference |= (expected.charCodeAt(i) || 0) ^ (received.charCodeAt(i) || 0);
  return difference === 0;
}

function reply_(ok) {
  return ContentService.createTextOutput(JSON.stringify({ ok: ok }))
    .setMimeType(ContentService.MimeType.JSON);
}

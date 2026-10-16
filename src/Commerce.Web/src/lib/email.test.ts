import { describe, expect, it } from 'vitest'
import { emailStatus, isValidEmail } from './email'

/** Same cases as tests/Commerce.Integration/EmailAddressRulesTests.cs: the web mirrors the server rule. */
describe('isValidEmail', () => {
  it.each([
    'ana@mail.com',
    'a.b+c@mail.com.ar',
    'ANA@MAIL.COM',
    '  ana@mail.com  ',
    "o'brien_99@sub-domain.example.org",
    'admin@vacaverde.local',
    'x@a1.io',
  ])('accepts %j', (value) => expect(isValidEmail(value)).toBe(true))

  it.each([
    'ana@',
    'ana@mail',
    'ana@@mail.com',
    'ana@-mail.com',
    'ana@mail-.com',
    'ana@mail.c',
    'ana@mail.c0m',
    'ana@mail..com',
    '@mail.com',
    'ana',
    '.ana@mail.com',
    'ana.@mail.com',
    'an..a@mail.com',
    'ana maria@mail.com',
    'ana@mail .com',
    'ana@mail.com.',
    'ana@.mail.com',
    '',
    '   ',
    null,
    undefined,
  ])('refuses %j', (value) => expect(isValidEmail(value)).toBe(false))

  it('refuses addresses longer than 254 characters', () => {
    const label = 'a'.repeat(60)
    const domain = Array(4).fill(label).join('.') + '.com' // 4 * 61 + 3 = 247
    expect(isValidEmail('ab@' + domain)).toBe(true) // 250
    expect(isValidEmail('abcdefg@' + domain)).toBe(false) // 255
  })

  it('refuses a local part longer than 64 characters', () => {
    expect(isValidEmail('a'.repeat(64) + '@mail.com')).toBe(true)
    expect(isValidEmail('a'.repeat(65) + '@mail.com')).toBe(false)
  })
})

describe('emailStatus', () => {
  it('is empty for a blank value, valid or invalid otherwise', () => {
    expect(emailStatus('  ')).toBe('empty')
    expect(emailStatus('ana@mail.com')).toBe('valid')
    expect(emailStatus('ana@')).toBe('invalid')
  })
})

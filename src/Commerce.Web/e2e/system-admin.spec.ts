import { expect, test } from '@playwright/test'
import { uniqueEmail } from './helpers'

test('a migrated system admin sees Organizations and can onboard an organization', async ({ page }) => {
  const organizationName = `Runtime onboarded ${Date.now()}`
  await page.goto('/login')
  await page.getByLabel('Email').fill(process.env.SYSADMIN_EMAIL!)
  await page.getByLabel('Password').fill(process.env.SYSADMIN_PASSWORD!)
  await page.getByRole('button', { name: /sign in/i }).click()
  await expect(page.getByRole('link', { name: 'Organizations' })).toBeVisible()
  await page.getByRole('link', { name: 'Organizations' }).click()
  await page.getByRole('button', { name: 'New organization' }).click()
  await page.getByLabel('Organization name').fill(organizationName)
  await page.getByLabel('Administrator email').fill(uniqueEmail('runtime-new-admin'))
  await page.getByLabel('Administrator password').fill('correct-horse-battery-staple')
  await page.getByRole('button', { name: 'Create organization' }).click()
  await expect(page.getByText(organizationName)).toBeVisible()
})

import { expect, test } from '@playwright/test'
import { seedUser, uniqueEmail } from './helpers'

test('a real business-admin can use Users and Branches but cannot reach Organizations', async ({ page, baseURL }) => {
  const password = 'correct-horse-battery-staple'
  const admin = await seedUser(baseURL!, { email: uniqueEmail('admin-console'), password })
  await page.goto('/login')
  await page.getByLabel('Email').fill(admin.email)
  await page.getByLabel('Password').fill(password)
  await page.getByRole('button', { name: /sign in/i }).click()
  await expect(page.getByRole('link', { name: 'Users' })).toBeVisible()
  await expect(page.getByRole('link', { name: 'Branches' })).toBeVisible()
  await expect(page.getByRole('link', { name: 'Organizations' })).not.toBeVisible()
  await page.getByRole('link', { name: 'Users' }).click()
  await page.getByLabel('User email').fill(uniqueEmail('staff'))
  await page.getByLabel('User password').fill(password)
  await page.getByRole('button', { name: 'Create user' }).click()
  await expect(page.getByText(/staff-/)).toBeVisible()
  await page.getByRole('link', { name: 'Branches' }).click()
  await page.getByLabel('Branch name').fill('Runtime branch')
  await page.getByRole('button', { name: 'Create branch' }).click()
  await expect(page.getByText('Runtime branch')).toBeVisible()
  await page.evaluate(() => { window.history.pushState({}, '', '/app/organizations'); window.dispatchEvent(new PopStateEvent('popstate')) })
  await expect(page).toHaveURL(/\/app\/catalog$/)
})
